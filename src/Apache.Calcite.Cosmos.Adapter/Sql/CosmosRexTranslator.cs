using System;
using System.Collections.Generic;
using System.Text;

using Apache.Calcite.Cosmos.Adapter.Internal;

using org.apache.calcite.rex;
using org.apache.calcite.sql;
using org.apache.calcite.sql.type;
using org.apache.calcite.util;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// Translates Calcite <see cref="RexNode"/> expressions into Cosmos SQL scalar expression text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Field references are resolved through an ordinal-indexed list of <see cref="CosmosPath"/>
    /// supplied by the caller, which knows how the input row type maps onto document paths.
    /// Literals are bound as parameters rather than inlined, so the generated text never varies
    /// with the data.
    /// </para>
    /// <para>
    /// Anything without a faithful Cosmos equivalent raises <see cref="CosmosTranslationException"/>.
    /// Declining is the correct outcome — the operator is then evaluated by Calcite in-process.
    /// An operator is only supported here when its Cosmos semantics are known to match; where
    /// they differ subtly (see remarks on individual cases) it is deliberately refused rather
    /// than approximated.
    /// </para>
    /// <para>
    /// Binary operations are fully parenthesized. Cosmos accepts redundant parentheses, and
    /// relying on them removes any dependence on its operator precedence table.
    /// </para>
    /// </remarks>
    public sealed class CosmosRexTranslator
    {

        readonly IReadOnlyList<CosmosPath> _fields;
        readonly CosmosParameterList _parameters;
        readonly RexBuilder _rexBuilder;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="rexBuilder">Used to expand <c>SEARCH</c> nodes back into comparison trees.</param>
        /// <param name="fields">Maps input field ordinals onto document paths.</param>
        /// <param name="parameters">Receives bound literal values.</param>
        /// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
        public CosmosRexTranslator(RexBuilder rexBuilder, IReadOnlyList<CosmosPath> fields, CosmosParameterList parameters)
        {
            _rexBuilder = rexBuilder ?? throw new ArgumentNullException(nameof(rexBuilder));
            _fields = fields ?? throw new ArgumentNullException(nameof(fields));
            _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        }

        /// <summary>
        /// Attempts to translate <paramref name="node"/>.
        /// </summary>
        /// <remarks>
        /// Parameters bound before a failure remain in the list. Callers that may discard the
        /// result should translate into a throwaway <see cref="CosmosParameterList"/> and merge
        /// on success.
        /// </remarks>
        /// <param name="node">The expression to translate.</param>
        /// <param name="expression">On success, the Cosmos SQL text.</param>
        /// <returns><c>true</c> if the expression was translated; otherwise <c>false</c>.</returns>
        public bool TryTranslate(RexNode node, out string? expression)
        {
            try
            {
                expression = Translate(node);
                return true;
            }
            catch (CosmosTranslationException)
            {
                expression = null;
                return false;
            }
        }

        /// <summary>
        /// Determines whether an expression denotes a document path, and if so what that path is.
        /// </summary>
        /// <remarks>
        /// Some clauses require a path rather than an arbitrary expression — <c>ORDER BY</c> keys
        /// must be resolvable against the container's indexes, for instance. Only a field
        /// reference or a chain of constant <c>ITEM</c> accessors over one qualifies.
        /// </remarks>
        /// <param name="node">The expression to inspect.</param>
        /// <param name="path">On success, the resolved path.</param>
        /// <returns><c>true</c> if the expression denotes a path; otherwise <c>false</c>.</returns>
        public bool TryResolvePath(RexNode node, out CosmosPath? path)
        {
            switch (node)
            {
                case RexInputRef inputRef when inputRef.getIndex() >= 0 && inputRef.getIndex() < _fields.Count:
                    path = _fields[inputRef.getIndex()];
                    return true;

                case RexCall call when KindOf(call) == SqlKind.__Enum.ITEM && call.getOperands().size() == 2:
                    if (TryResolvePath(Operand(call, 0), out var basePath) == false || Operand(call, 1) is not RexLiteral accessor)
                        break;

                    object? value;
                    try
                    {
                        value = GetLiteralValue(accessor);
                    }
                    catch (CosmosTranslationException)
                    {
                        break;
                    }

                    if (value is string name)
                    {
                        path = basePath!.Property(name);
                        return true;
                    }

                    if (TryGetArrayIndex(value, out var index))
                    {
                        path = basePath!.Index(index);
                        return true;
                    }

                    break;
            }

            path = null;
            return false;
        }

        /// <summary>
        /// Translates <paramref name="node"/>.
        /// </summary>
        /// <param name="node">The expression to translate.</param>
        /// <returns>The Cosmos SQL text.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="node"/> is <c>null</c>.</exception>
        /// <exception cref="CosmosTranslationException">The expression has no Cosmos equivalent.</exception>
        public string Translate(RexNode node)
        {
            if (node is null)
                throw new ArgumentNullException(nameof(node));

            // Calcite aggressively rewrites comparison chains into SEARCH over a Sarg. Expanding
            // first keeps range and IN predicates pushable instead of uniformly declined.
            var expanded = RexUtil.expandSearch(_rexBuilder, null, node);

            var builder = new StringBuilder();
            Write(builder, expanded);
            return builder.ToString();
        }

        void Write(StringBuilder builder, RexNode node)
        {
            switch (node)
            {
                case RexInputRef inputRef:
                    WriteInputRef(builder, inputRef);
                    break;
                case RexLiteral literal:
                    WriteLiteral(builder, literal);
                    break;
                case RexCall call:
                    WriteCall(builder, call);
                    break;
                default:
                    throw new CosmosTranslationException($"Unsupported expression '{node.getKind().name()}' of type '{node.GetType().Name}'.");
            }
        }

        void WriteInputRef(StringBuilder builder, RexInputRef inputRef)
        {
            var index = inputRef.getIndex();
            if (index < 0 || index >= _fields.Count)
                throw new CosmosTranslationException($"Field ordinal {index} is not bound to a document path.");

            _fields[index].WriteTo(builder);
        }

        void WriteLiteral(StringBuilder builder, RexLiteral literal)
        {
            // Bind rather than inline, so that statement text is independent of data.
            builder.Append(_parameters.Add(GetLiteralValue(literal)));
        }

        /// <summary>
        /// Converts a Calcite literal into the CLR value to bind.
        /// </summary>
        /// <remarks>
        /// Temporal literals are refused. Cosmos JSON has no date or time type — dates are ISO
        /// strings or epoch numbers by application convention, and nothing in the container
        /// declares which. Guessing an encoding would silently return wrong rows.
        /// </remarks>
        static object? GetLiteralValue(RexLiteral literal)
        {
            if (literal.isNull())
                return null;

            var type = literal.getTypeName();

            if (type == SqlTypeName.BOOLEAN)
                return ((java.lang.Boolean)literal.getValue()).booleanValue();

            if (type == SqlTypeName.CHAR || type == SqlTypeName.VARCHAR)
                return literal.getValue() is NlsString s ? s.getValue() : literal.getValue()?.ToString();

            // Calcite is not consistent about the boxed representation of numeric literals:
            // exact literals arrive as BigDecimal, while those built through makeApproxLiteral
            // arrive as java.lang.Double. Accept either rather than assuming one.
            if (type == SqlTypeName.TINYINT || type == SqlTypeName.SMALLINT || type == SqlTypeName.INTEGER || type == SqlTypeName.BIGINT)
            {
                return literal.getValue() switch
                {
                    java.math.BigDecimal bd => bd.longValueExact(),
                    java.lang.Long l => l.longValue(),
                    java.lang.Integer i => (long)i.intValue(),
                    var v => throw new CosmosTranslationException($"Unexpected representation '{v?.GetType().Name}' for an integer literal."),
                };
            }

            if (type == SqlTypeName.DECIMAL)
                return BigDecimalConverter.ToDecimal((java.math.BigDecimal)literal.getValue());

            if (type == SqlTypeName.FLOAT || type == SqlTypeName.REAL || type == SqlTypeName.DOUBLE)
            {
                return literal.getValue() switch
                {
                    java.math.BigDecimal bd => bd.doubleValue(),
                    java.lang.Double d => d.doubleValue(),
                    java.lang.Float f => (double)f.floatValue(),
                    var v => throw new CosmosTranslationException($"Unexpected representation '{v?.GetType().Name}' for an approximate literal."),
                };
            }

            throw new CosmosTranslationException($"Unsupported literal type '{type.getName()}'.");
        }

        void WriteCall(StringBuilder builder, RexCall call)
        {
            switch (KindOf(call))
            {
                case SqlKind.__Enum.EQUALS:
                    WriteBinary(builder, call, "=");
                    break;
                case SqlKind.__Enum.NOT_EQUALS:
                    WriteBinary(builder, call, "!=");
                    break;
                case SqlKind.__Enum.LESS_THAN:
                    WriteBinary(builder, call, "<");
                    break;
                case SqlKind.__Enum.LESS_THAN_OR_EQUAL:
                    WriteBinary(builder, call, "<=");
                    break;
                case SqlKind.__Enum.GREATER_THAN:
                    WriteBinary(builder, call, ">");
                    break;
                case SqlKind.__Enum.GREATER_THAN_OR_EQUAL:
                    WriteBinary(builder, call, ">=");
                    break;
                case SqlKind.__Enum.AND:
                    WriteChain(builder, call, "AND");
                    break;
                case SqlKind.__Enum.OR:
                    WriteChain(builder, call, "OR");
                    break;
                case SqlKind.__Enum.PLUS:
                    WriteBinary(builder, call, "+");
                    break;
                case SqlKind.__Enum.MINUS:
                    WriteBinary(builder, call, "-");
                    break;
                case SqlKind.__Enum.TIMES:
                    WriteBinary(builder, call, "*");
                    break;
                case SqlKind.__Enum.DIVIDE:
                    WriteBinary(builder, call, "/");
                    break;
                case SqlKind.__Enum.MOD:
                    WriteBinary(builder, call, "%");
                    break;
                case SqlKind.__Enum.NOT:
                    RequireOperandCount(call, 1);
                    builder.Append("(NOT ");
                    Write(builder, Operand(call, 0));
                    builder.Append(')');
                    break;
                case SqlKind.__Enum.MINUS_PREFIX:
                    RequireOperandCount(call, 1);
                    builder.Append("(-");
                    Write(builder, Operand(call, 0));
                    builder.Append(')');
                    break;
                case SqlKind.__Enum.IS_NULL:
                    WriteIsNull(builder, call, negated: false);
                    break;
                case SqlKind.__Enum.IS_NOT_NULL:
                    WriteIsNull(builder, call, negated: true);
                    break;
                case SqlKind.__Enum.LIKE:
                    WriteLike(builder, call);
                    break;
                case SqlKind.__Enum.ITEM:
                    WriteItem(builder, call);
                    break;
                case SqlKind.__Enum.CASE:
                    WriteCase(builder, call);
                    break;
                default:
                    throw new CosmosTranslationException($"Unsupported operator '{call.getOperator().getName()}' ({call.getKind().name()}).");
            }
        }

        void WriteBinary(StringBuilder builder, RexCall call, string op)
        {
            RequireOperandCount(call, 2);

            builder.Append('(');
            Write(builder, Operand(call, 0));
            builder.Append(' ').Append(op).Append(' ');
            Write(builder, Operand(call, 1));
            builder.Append(')');
        }

        void WriteChain(StringBuilder builder, RexCall call, string op)
        {
            var operands = call.getOperands();
            if (operands.size() < 2)
                throw new CosmosTranslationException($"Operator '{op}' requires at least two operands.");

            builder.Append('(');

            for (var i = 0; i < operands.size(); i++)
            {
                if (i > 0)
                    builder.Append(' ').Append(op).Append(' ');

                Write(builder, (RexNode)operands.get(i));
            }

            builder.Append(')');
        }

        /// <summary>
        /// Writes a null test.
        /// </summary>
        /// <remarks>
        /// Cosmos distinguishes an absent property (<c>undefined</c>) from one present with a null
        /// value, whereas SQL has only <c>NULL</c>. Both Cosmos states must therefore be tested,
        /// or a filter on a property that is simply missing from a document would not match.
        /// </remarks>
        void WriteIsNull(StringBuilder builder, RexCall call, bool negated)
        {
            RequireOperandCount(call, 1);

            var operand = new StringBuilder();
            Write(operand, Operand(call, 0));
            var text = operand.ToString();

            if (negated)
                builder.Append("(IS_DEFINED(").Append(text).Append(") AND NOT IS_NULL(").Append(text).Append("))");
            else
                builder.Append("(NOT IS_DEFINED(").Append(text).Append(") OR IS_NULL(").Append(text).Append("))");
        }

        /// <summary>
        /// Writes a <c>LIKE</c> predicate.
        /// </summary>
        /// <remarks>
        /// The three-operand form carries an <c>ESCAPE</c> clause, which is refused rather than
        /// dropped: silently ignoring it would change which rows match.
        /// </remarks>
        void WriteLike(StringBuilder builder, RexCall call)
        {
            if (call.getOperands().size() != 2)
                throw new CosmosTranslationException("LIKE with an ESCAPE clause is not supported.");

            WriteBinary(builder, call, "LIKE");
        }

        /// <summary>
        /// Writes a map or array element access.
        /// </summary>
        /// <remarks>
        /// <c>ITEM</c> is the operator the map row model depends on: a reference to a document
        /// property arrives as <c>ITEM(&lt;map&gt;, 'name')</c> and must become a path extension
        /// rather than a function call. Only constant accessors can be resolved this way, since
        /// Cosmos paths are static.
        /// </remarks>
        void WriteItem(StringBuilder builder, RexCall call)
        {
            RequireOperandCount(call, 2);

            if (Operand(call, 1) is not RexLiteral accessor)
                throw new CosmosTranslationException("ITEM requires a constant accessor.");

            // The base must itself be a path, otherwise appending a segment would produce
            // nonsense such as "@p0.name" from a bound parameter. Calcite's own operand type
            // checking rejects many such calls before they reach here, but not all of them.
            var target = Operand(call, 0);
            if (target is not RexInputRef && (target is not RexCall inner || KindOf(inner) != SqlKind.__Enum.ITEM))
                throw new CosmosTranslationException("ITEM may only be applied to a field reference or another ITEM.");

            Write(builder, target);

            var value = GetLiteralValue(accessor);

            if (value is string name)
                CosmosSql.WritePropertyAccess(builder, name);
            else if (TryGetArrayIndex(value, out var index))
                CosmosSql.WriteIndexAccess(builder, index);
            else
                throw new CosmosTranslationException("ITEM accessor must be a string property name or a non-negative array index.");
        }

        /// <summary>
        /// Recognizes an array subscript, which may arrive as any of several numeric types
        /// depending on how the literal was built.
        /// </summary>
        static bool TryGetArrayIndex(object? value, out int index)
        {
            switch (value)
            {
                case long l when l >= 0 && l <= int.MaxValue:
                    index = (int)l;
                    return true;
                case int i when i >= 0:
                    index = i;
                    return true;
                case decimal m when m >= 0 && m <= int.MaxValue && decimal.Truncate(m) == m:
                    index = (int)m;
                    return true;
                default:
                    index = 0;
                    return false;
            }
        }

        /// <summary>
        /// Writes a <c>CASE</c> expression as a chain of ternary conditionals.
        /// </summary>
        /// <remarks>
        /// Cosmos has no <c>CASE</c>. Its ternary operator is semantically equivalent for the
        /// searched form Calcite produces: operands alternate condition and result, with a final
        /// else branch.
        /// </remarks>
        void WriteCase(StringBuilder builder, RexCall call)
        {
            var operands = call.getOperands();
            if (operands.size() < 3 || operands.size() % 2 == 0)
                throw new CosmosTranslationException("CASE must have an odd number of operands of at least three.");

            var depth = 0;

            for (var i = 0; i + 1 < operands.size(); i += 2)
            {
                builder.Append('(');
                Write(builder, (RexNode)operands.get(i));
                builder.Append(" ? ");
                Write(builder, (RexNode)operands.get(i + 1));
                builder.Append(" : ");
                depth++;
            }

            Write(builder, (RexNode)operands.get(operands.size() - 1));
            builder.Append(')', depth);
        }

        /// <summary>
        /// Projects a call's <see cref="SqlKind"/> onto the CLR enumeration IKVM generates for it,
        /// so that dispatch is compile-time checked rather than string-matched.
        /// </summary>
        static SqlKind.__Enum KindOf(RexCall call) => (SqlKind.__Enum)call.getKind().ordinal();

        /// <summary>
        /// Returns an operand as a <see cref="RexNode"/>. Calcite's operand lists are raw Java
        /// collections, which surface as <see cref="object"/>.
        /// </summary>
        static RexNode Operand(RexCall call, int index) => (RexNode)call.getOperands().get(index);

        static void RequireOperandCount(RexCall call, int count)
        {
            if (call.getOperands().size() != count)
                throw new CosmosTranslationException($"Operator '{call.getOperator().getName()}' expects {count} operand(s), found {call.getOperands().size()}.");
        }

    }

}
