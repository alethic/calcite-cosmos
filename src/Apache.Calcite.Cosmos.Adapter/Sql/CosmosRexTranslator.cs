using System;
using System.Collections.Generic;
using System.Text;

using Apache.Calcite.Cosmos.Adapter.Internal;

using org.apache.calcite.rex;
using org.apache.calcite.sql;
using org.apache.calcite.sql.fun;
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

        readonly IReadOnlyList<CosmosPath?> _fields;

        /// <summary>
        /// The correlation variable whose row is the one <see cref="_fields"/> describes, where the
        /// caller has one.
        /// </summary>
        readonly org.apache.calcite.rel.core.CorrelationId? _ownRow;

        /// <summary>
        /// Whether a scoring function is currently legal, which is only while rendering a rank clause.
        /// </summary>
        bool _scoring;
        readonly CosmosParameterList _parameters;
        readonly RexBuilder _rexBuilder;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="rexBuilder">Used to expand <c>SEARCH</c> nodes back into comparison trees.</param>
        /// <param name="fields">Maps input field ordinals onto document paths.</param>
        /// <param name="parameters">Receives bound literal values.</param>
        /// <param name="ownRow">
        /// The correlation variable that stands for the very row <paramref name="fields"/> describes,
        /// where one is in scope. A lateral traversal is correlated on its own input, so its array
        /// expression addresses this input through such a variable; nothing else does.
        /// </param>
        /// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
        public CosmosRexTranslator(RexBuilder rexBuilder, IReadOnlyList<CosmosPath?> fields, CosmosParameterList parameters, org.apache.calcite.rel.core.CorrelationId? ownRow = null)
        {
            _rexBuilder = rexBuilder ?? throw new ArgumentNullException(nameof(rexBuilder));
            _fields = fields ?? throw new ArgumentNullException(nameof(fields));
            _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
            _ownRow = ownRow;
        }

        /// <summary>
        /// Determines whether a correlation variable stands for this input's own row.
        /// </summary>
        /// <remarks>
        /// The distinction matters because the two cases are the same shape of expression and mean
        /// opposite things. A lateral traversal correlates an input on itself, so
        /// <c>$cor0._MAP['tags']</c> under one denotes a path of the document being scanned. A join
        /// correlates it on the <em>other</em> side, and there the identical expression denotes a
        /// value of a row this statement knows nothing about — which would resolve against these
        /// bindings to a plausible, wrong document path. Where the caller has not said which variable
        /// is its own, none is.
        /// </remarks>
        bool IsOwnRow(RexCorrelVariable variable)
        {
            return _ownRow is not null && _ownRow.equals(variable.id);
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
                // A null binding is a field with no document path — a computed projection — and does
                // not resolve. The caller declines the operator, exactly as it would for an ordinal
                // that was never bound.
                case RexInputRef inputRef when inputRef.getIndex() >= 0 && inputRef.getIndex() < _fields.Count:
                    path = _fields[inputRef.getIndex()];
                    return path is not null;

                // A field of a correlation variable standing for this input's own row addresses that
                // row, so it resolves against the same bindings as a plain field reference. This is
                // how the array expression of a lateral unnest arrives. A variable standing for
                // anything else is refused — see IsOwnRow.
                case RexFieldAccess access when access.getReferenceExpr() is RexCorrelVariable variable && IsOwnRow(variable)
                    && access.getField().getIndex() >= 0 && access.getField().getIndex() < _fields.Count:
                    path = _fields[access.getField().getIndex()];
                    return path is not null;

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

            // Unbound because the field is a computed projection. Cosmos cannot address one — a
            // projection alias is not visible to WHERE or ORDER BY — so the operator reading it is
            // declined and Calcite evaluates it in-process.
            var path = _fields[index]
                ?? throw new CosmosTranslationException($"Field ordinal {index} is a computed projection and has no document path.");

            path.WriteTo(builder);
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
        internal static object? GetLiteralValue(RexLiteral literal)
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
                case SqlKind.__Enum.TRIM:
                    WriteTrim(builder, call);
                    break;
                case SqlKind.__Enum.FLOOR:
                    RequireOperandCount(call, 1);
                    WriteFunctionCall(builder, call, "FLOOR");
                    break;
                case SqlKind.__Enum.CEIL:
                    // Cosmos spells it CEILING; CEIL is rejected.
                    RequireOperandCount(call, 1);
                    WriteFunctionCall(builder, call, "CEILING");
                    break;
                default:
                    // Most scalar functions are OTHER_FUNCTION, but several carry a dedicated kind
                    // — CHAR_LENGTH and POSITION among them — so dispatch on the name for anything
                    // whose structure does not need special handling.
                    WriteNamedFunction(builder, call);
                    break;
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
        /// <para>
        /// The three-operand form carries an <c>ESCAPE</c> clause, which is refused rather than
        /// dropped: silently ignoring it would change which rows match.
        /// </para>
        /// <para>
        /// The pattern must be a literal, because Cosmos <c>LIKE</c> is not SQL <c>LIKE</c>: Cosmos
        /// additionally reads <c>[…]</c> as a character range where SQL matches the brackets
        /// literally. A literal pattern is checked for brackets and declined when it has any; a
        /// computed pattern cannot be checked and is declined whole, since pushing it would change
        /// which rows match whenever a value contains one.
        /// </para>
        /// <para>
        /// A literal pattern whose only wildcard is a single trailing <c>%</c> is a prefix match,
        /// rendered as <c>STARTSWITH</c> — which the index serves, where <c>LIKE</c> is a scan.
        /// </para>
        /// </remarks>
        void WriteLike(StringBuilder builder, RexCall call)
        {
            if (call.getOperands().size() != 2)
                throw new CosmosTranslationException("LIKE with an ESCAPE clause is not supported.");

            if (Operand(call, 1) is not RexLiteral patternLiteral || GetLiteralValue(patternLiteral) is not string pattern)
                throw new CosmosTranslationException("LIKE with a computed pattern is not supported: Cosmos gives '[' a meaning SQL does not, and only a literal pattern can be checked for one.");

            if (pattern.IndexOfAny(new[] { '[', ']' }) >= 0)
                throw new CosmosTranslationException("LIKE with a bracket in the pattern is not supported: Cosmos reads a character range where SQL reads the brackets literally.");

            if (pattern.EndsWith("%", StringComparison.Ordinal) &&
                pattern.IndexOfAny(new[] { '%', '_' }) == pattern.Length - 1)
            {
                builder.Append("STARTSWITH(");
                Write(builder, Operand(call, 0));
                builder.Append(", ").Append(_parameters.Add(pattern.Substring(0, pattern.Length - 1))).Append(')');
                return;
            }

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
        /// Scalar functions whose Cosmos counterpart takes the same arguments in the same order
        /// and means the same thing, mapped from the SQL name to the Cosmos name.
        /// </summary>
        /// <remarks>
        /// Verified against the service. Functions needing an argument adjustment are handled
        /// separately below; anything absent from both is declined.
        /// </remarks>
        static readonly Dictionary<string, (string Name, int MinArgs, int MaxArgs)> DirectFunctions = new(StringComparer.Ordinal)
        {
            ["UPPER"] = ("UPPER", 1, 1),
            ["LOWER"] = ("LOWER", 1, 1),
            ["LENGTH"] = ("LENGTH", 1, 1),
            ["CHAR_LENGTH"] = ("LENGTH", 1, 1),
            ["CHARACTER_LENGTH"] = ("LENGTH", 1, 1),
            ["REPLACE"] = ("REPLACE", 3, 3),
            ["CONCAT"] = ("CONCAT", 2, int.MaxValue),
            ["||"] = ("CONCAT", 2, int.MaxValue),
            ["ABS"] = ("ABS", 1, 1),
            ["EXP"] = ("EXP", 1, 1),
            ["LN"] = ("LOG", 1, 1),
            ["LOG10"] = ("LOG10", 1, 1),
            ["POWER"] = ("POWER", 2, 2),
            ["SQRT"] = ("SQRT", 1, 1),
            ["SIGN"] = ("SIGN", 1, 1),
            ["ROUND"] = ("ROUND", 1, 1),
            // Cosmos carries the whole trigonometric set under the names SQL uses, ATAN2 excepted:
            // Cosmos spells it ATN2, after T-SQL.
            ["SIN"] = ("SIN", 1, 1),
            ["COS"] = ("COS", 1, 1),
            ["TAN"] = ("TAN", 1, 1),
            ["COT"] = ("COT", 1, 1),
            // ASIN and ACOS carry a domain, and Cosmos fails the whole query for a value outside it
            // rather than yielding undefined — measured, along with SQRT(-1), which does the same and
            // which this adapter has always pushed. LOG(0) is accepted. So this is one property of the
            // service rather than a fact about these two, and they are treated as SQRT already is; see
            // OutOfDomainArithmeticFailsTheQuery, which pins the measurement, and DESIGN.md, which
            // records that the choice to live with it is a choice.
            ["ASIN"] = ("ASIN", 1, 1),
            ["ACOS"] = ("ACOS", 1, 1),
            ["ATAN"] = ("ATAN", 1, 1),
            ["ATAN2"] = ("ATN2", 2, 2),
            ["DEGREES"] = ("DEGREES", 1, 1),
            ["RADIANS"] = ("RADIANS", 1, 1),
            // Niladic, and written PI() at both ends.
            ["PI"] = ("PI", 0, 0),
            // The two-argument SQL form — truncate to a number of decimal places — is left out
            // rather than guessed at, the arity of Cosmos's TRUNC not having been verified.
            ["TRUNCATE"] = ("TRUNC", 1, 1),
            // Rankable, but not restricted to a rank clause the way a full text score is: the reference
            // projects it. So it renders wherever any other function does, and IsScoringFunction lets an
            // ORDER BY over it reach the clause as well.
            ["VECTORDISTANCE"] = ("VECTORDISTANCE", 2, 4),
            // String functions SQL and Cosmos spell alike and mean alike. LEFT and RIGHT clamp rather
            // than fail on a count longer than the string at both ends; REVERSE is a reversal.
            ["LEFT"] = ("LEFT", 2, 2),
            ["RIGHT"] = ("RIGHT", 2, 2),
            ["REVERSE"] = ("REVERSE", 1, 1),
            // SQL repeats a string with REPEAT and Cosmos with REPLICATE. Same arguments, same order,
            // same meaning.
            ["REPEAT"] = ("REPLICATE", 2, 2),
            // The array functions, mapped from their SQL counterparts. ARRAY_SLICE differs only in
            // index origin and is adjusted like SUBSTRING, below; these three agree outright.
            ["ARRAY_CONCAT"] = ("ARRAY_CONCAT", 2, int.MaxValue),
            ["ARRAY_INTERSECT"] = ("SETINTERSECT", 2, 2),
            ["ARRAY_UNION"] = ("SETUNION", 2, 2),
            // Under its own name, and this one is not an index origin: a regular expression dialect
            // is a semantic difference a query cannot see. Cosmos documents PCRE with constructs it
            // does not support, so REGEXP_LIKE and this are two languages, not two spellings — the
            // LIKE bracket measurement is the standing argument.
            ["REGEXMATCH"] = ("REGEXMATCH", 2, 3),
            // The JSON conversions. Cosmos spells these in camel case and the service is
            // case-insensitive about function names, but they are emitted as documented.
            ["ToString"] = ("ToString", 1, 1),
            ["StringToNumber"] = ("StringToNumber", 1, 1),
            ["StringToObject"] = ("StringToObject", 1, 1),
            ["StringToArray"] = ("StringToArray", 1, 1),
            ["StringToBoolean"] = ("StringToBoolean", 1, 1),
            ["ObjectToArray"] = ("ObjectToArray", 1, 1),
            // The type tests. Their argument is an ordinary expression rather than a path, so they need
            // nothing beyond the name — which is already the Cosmos one, these operators being this
            // adapter's own rather than translations of a SQL counterpart. See CosmosOperators.
            ["IS_DEFINED"] = ("IS_DEFINED", 1, 1),
            ["IS_ARRAY"] = ("IS_ARRAY", 1, 1),
            ["IS_BOOL"] = ("IS_BOOL", 1, 1),
            ["IS_NULL"] = ("IS_NULL", 1, 1),
            ["IS_NUMBER"] = ("IS_NUMBER", 1, 1),
            ["IS_OBJECT"] = ("IS_OBJECT", 1, 1),
            ["IS_PRIMITIVE"] = ("IS_PRIMITIVE", 1, 1),
            ["IS_STRING"] = ("IS_STRING", 1, 1),
        };

        /// <summary>
        /// Writes a function whose Cosmos form differs from SQL's only in name.
        /// </summary>
        void WriteNamedFunction(StringBuilder builder, RexCall call)
        {
            var name = call.getOperator().getName();

            switch (name)
            {
                case "SUBSTRING":
                    WriteSubstring(builder, call);
                    return;
                case "ARRAY_SLICE":
                    WriteArraySlice(builder, call);
                    return;
                case "POSITION":
                    WritePosition(builder, call);
                    return;
                case "CARDINALITY":
                    WriteCardinality(builder, call);
                    return;
                case "MEMBER OF":
                    WriteMemberOf(builder, call);
                    return;
                case "FULLTEXTCONTAINS":
                case "FULLTEXTCONTAINSALL":
                case "FULLTEXTCONTAINSANY":
                    WriteFullTextPredicate(builder, call, name);
                    return;
                case "FULLTEXTSCORE":
                    if (_scoring == false)
                        throw new CosmosTranslationException($"'{name}' is only legal in an ORDER BY RANK clause.");

                    // Same shape as the predicates: a property path, then the terms.
                    WriteFullTextPredicate(builder, call, name);
                    return;

                case "RRF":
                    if (_scoring == false)
                        throw new CosmosTranslationException($"'{name}' is only legal in an ORDER BY RANK clause.");

                    WriteRrf(builder, call);
                    return;
            }

            if (DirectFunctions.TryGetValue(name, out var mapping) == false)
                throw new CosmosTranslationException($"Unsupported function '{name}'.");

            var count = call.getOperands().size();
            if (count < mapping.MinArgs || count > mapping.MaxArgs)
                throw new CosmosTranslationException($"Function '{name}' with {count} argument(s) has no Cosmos equivalent.");

            WriteFunctionCall(builder, call, mapping.Name);
        }

        /// <summary>
        /// Writes a call as <c>NAME(arg, …)</c> over all of its operands.
        /// </summary>
        void WriteFunctionCall(StringBuilder builder, RexCall call, string name)
        {
            builder.Append(name).Append('(');

            for (var i = 0; i < call.getOperands().size(); i++)
            {
                if (i > 0)
                    builder.Append(", ");

                Write(builder, Operand(call, i));
            }

            builder.Append(')');
        }

        /// <summary>
        /// Writes <c>SUBSTRING</c>, adjusting for the differing origin.
        /// </summary>
        /// <remarks>
        /// SQL positions are one-based and Cosmos's are zero-based: over <c>"Hello World"</c>,
        /// <c>SUBSTRING(s, 0, 5)</c> yields <c>"Hello"</c> where SQL's <c>FROM 1</c> does. The
        /// start is therefore emitted as <c>start - 1</c>.
        /// <para>
        /// Cosmos requires a length, so the two-argument SQL form — take the rest of the string —
        /// is declined rather than approximated with an over-long length.
        /// </para>
        /// </remarks>
        void WriteSubstring(StringBuilder builder, RexCall call)
        {
            if (call.getOperands().size() != 3)
                throw new CosmosTranslationException("SUBSTRING without an explicit length has no Cosmos equivalent.");

            builder.Append("SUBSTRING(");
            Write(builder, Operand(call, 0));
            builder.Append(", (");
            Write(builder, Operand(call, 1));
            builder.Append(" - 1), ");
            Write(builder, Operand(call, 2));
            builder.Append(')');
        }

        /// <summary>
        /// Writes <c>ARRAY_SLICE</c>, adjusting for the differing origin.
        /// </summary>
        /// <remarks>
        /// Both take <c>(array, start, length)</c> and differ only in where counting begins —
        /// Calcite's is one-based, Cosmos's zero-based — which is a translation detail rather than
        /// a difference in meaning, and is emitted as <c>start - 1</c> exactly as
        /// <see cref="WriteSubstring"/> does. The shift is checked rather than reasoned: the
        /// differential corpus slices the same array both ways, so an off-by-one shows as different
        /// elements rather than as an error.
        /// <para>
        /// The two-argument form is accepted because Cosmos accepts it — the rest of the array —
        /// though the library's counterpart takes exactly three, so no SQL statement produces it
        /// today.
        /// </para>
        /// </remarks>
        void WriteArraySlice(StringBuilder builder, RexCall call)
        {
            var operands = call.getOperands();
            if (operands.size() is not (2 or 3))
                throw new CosmosTranslationException("ARRAY_SLICE takes an array, a start, and an optional length.");

            builder.Append("ARRAY_SLICE(");
            Write(builder, Operand(call, 0));
            builder.Append(", (");
            Write(builder, Operand(call, 1));
            builder.Append(" - 1)");

            if (operands.size() == 3)
            {
                builder.Append(", ");
                Write(builder, Operand(call, 2));
            }

            builder.Append(')');
        }

        /// <summary>
        /// Writes <c>POSITION</c> in terms of <c>INDEX_OF</c>.
        /// </summary>
        /// <remarks>
        /// <c>INDEX_OF</c> is zero-based and yields <c>-1</c> when absent, so adding one reproduces
        /// SQL exactly on both counts: a match at SQL position 7 and zero for no match.
        /// </remarks>
        void WritePosition(StringBuilder builder, RexCall call)
        {
            if (call.getOperands().size() != 2)
                throw new CosmosTranslationException("POSITION with a start offset has no Cosmos equivalent.");

            // SQL is POSITION(substring IN string); INDEX_OF takes the string first.
            builder.Append("(INDEX_OF(");
            Write(builder, Operand(call, 1));
            builder.Append(", ");
            Write(builder, Operand(call, 0));
            builder.Append(") + 1)");
        }

        /// <summary>
        /// Writes <c>CARDINALITY</c> in terms of <c>ARRAY_LENGTH</c>.
        /// </summary>
        /// <remarks>
        /// SQL defines <c>CARDINALITY</c> over a collection <em>or a map</em>, and Cosmos counts only an
        /// array. Counting a map's properties has no Cosmos form — the map column is the whole document,
        /// so the question is real and simply unanswerable here — and the map case is declined rather
        /// than emitted as an array count, which would report nothing meaningful.
        /// </remarks>
        void WriteCardinality(StringBuilder builder, RexCall call)
        {
            RequireOperandCount(call, 1);

            var typeName = Operand(call, 0).getType().getSqlTypeName();
            if (typeName != SqlTypeName.ARRAY && typeName != SqlTypeName.MULTISET)
                throw new CosmosTranslationException($"CARDINALITY over {typeName.name()} has no Cosmos equivalent; ARRAY_LENGTH counts an array.");

            WriteFunctionCall(builder, call, "ARRAY_LENGTH");
        }

        /// <summary>
        /// Writes <c>MEMBER OF</c> in terms of <c>ARRAY_CONTAINS</c>.
        /// </summary>
        /// <remarks>
        /// The operands are the other way round: SQL is <c>&lt;value&gt; MEMBER OF &lt;multiset&gt;</c> and
        /// Cosmos takes the array first. The optional third argument of <c>ARRAY_CONTAINS</c>, which asks
        /// for a partial match of an object, is not emitted — <c>MEMBER OF</c> means full equality.
        /// </remarks>
        void WriteMemberOf(StringBuilder builder, RexCall call)
        {
            RequireOperandCount(call, 2);

            builder.Append("ARRAY_CONTAINS(");
            Write(builder, Operand(call, 1));
            builder.Append(", ");
            Write(builder, Operand(call, 0));
            builder.Append(')');
        }

        /// <summary>
        /// Translates a scoring function for an <c>ORDER BY RANK</c> clause.
        /// </summary>
        /// <remarks>
        /// The one context in which <c>FULLTEXTSCORE</c> and <c>RRF</c> are legal. <see cref="Translate"/>
        /// refuses them, because everywhere it is used — a predicate, a projection, an ordinary sort key
        /// — is a place the service rejects them, and a refusal costs a pushdown where a rendered
        /// statement would cost the query.
        /// </remarks>
        /// <param name="node">The scoring function.</param>
        /// <returns>The Cosmos SQL text.</returns>
        /// <exception cref="CosmosTranslationException">The expression is not a scoring function, or has no Cosmos equivalent.</exception>
        public string TranslateRank(RexNode node)
        {
            if (node is null)
                throw new ArgumentNullException(nameof(node));

            if (IsScoringFunction(node) == false)
                throw new CosmosTranslationException("An ORDER BY RANK clause takes a scoring function.");

            _scoring = true;

            try
            {
                var builder = new StringBuilder();
                Write(builder, node);
                return builder.ToString();
            }
            finally
            {
                _scoring = false;
            }
        }

        /// <summary>
        /// Determines whether an expression is one of the functions <c>ORDER BY RANK</c> ranks by.
        /// </summary>
        /// <param name="node">The expression to test.</param>
        /// <returns><c>true</c> if it is a scoring function.</returns>
        public static bool IsScoringFunction(RexNode? node)
        {
            return node is RexCall call && call.getOperator().getName() switch
            {
                "FULLTEXTSCORE" or "RRF" or "VECTORDISTANCE" => true,
                _ => false,
            };
        }

        /// <summary>
        /// Writes <c>RRF</c>, whose arguments are themselves scoring functions.
        /// </summary>
        /// <remarks>
        /// Two or more of them, optionally followed by an array of weights. Only the scoring functions
        /// are checked for here — a trailing weights array is an ordinary expression and renders as one —
        /// so an argument that is neither is refused by the recursive call rather than by a count.
        /// </remarks>
        void WriteRrf(StringBuilder builder, RexCall call)
        {
            var operands = call.getOperands();
            if (operands.size() < 2)
                throw new CosmosTranslationException("RRF fuses two or more scoring functions.");

            builder.Append("RRF(");

            for (var i = 0; i < operands.size(); i++)
            {
                if (i > 0)
                    builder.Append(", ");

                Write(builder, Operand(call, i));
            }

            builder.Append(')');
        }

        /// <summary>
        /// Writes a full text predicate.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>FULLTEXTCONTAINS(&lt;property_path&gt;, &lt;string_expr&gt;)</c> and the <c>ALL</c> and
        /// <c>ANY</c> forms, which take one or more keywords. The rendered form is the SQL form: the
        /// arguments are already in the service's order.
        /// </para>
        /// <para>
        /// The first argument is a <em>property path</em> and not an expression, so a call over anything
        /// that does not resolve to one is declined rather than rendered — the service would reject it.
        /// The keywords are ordinary expressions and bind as parameters like any other literal.
        /// </para>
        /// </remarks>
        void WriteFullTextPredicate(StringBuilder builder, RexCall call, string name)
        {
            var operands = call.getOperands();
            if (operands.size() < 2)
                throw new CosmosTranslationException($"'{name}' expects a property path and at least one keyword.");

            if (TryResolvePath(Operand(call, 0), out var path) == false || path is null)
                throw new CosmosTranslationException($"The first argument of '{name}' must be a document path.");

            builder.Append(name).Append('(');
            path.WriteTo(builder);

            for (var i = 1; i < operands.size(); i++)
            {
                builder.Append(", ");
                Write(builder, Operand(call, i));
            }

            builder.Append(')');
        }

        /// <summary>
        /// Writes <c>TRIM</c>, whose specification decides which of Cosmos's three functions applies.
        /// </summary>
        /// <remarks>
        /// Calcite carries the operands as specification, characters, string — the specification being a
        /// symbol literal of <c>BOTH</c>, <c>LEADING</c> or <c>TRAILING</c>, which pick <c>TRIM</c>,
        /// <c>LTRIM</c> and <c>RTRIM</c>.
        /// <para>
        /// Only trimming spaces is translated. Cosmos's two-argument forms have not been verified, and
        /// emitting the one-argument form for <c>TRIM(LEADING 'x' FROM s)</c> would silently trim
        /// something else.
        /// </para>
        /// </remarks>
        void WriteTrim(StringBuilder builder, RexCall call)
        {
            RequireOperandCount(call, 3);

            if (Operand(call, 0) is not RexLiteral specification || specification.getValue() is not SqlTrimFunction.Flag flag)
                throw new CosmosTranslationException("TRIM without a constant specification has no Cosmos equivalent.");

            if (Operand(call, 1) is not RexLiteral characters || GetLiteralValue(characters) as string != " ")
                throw new CosmosTranslationException("TRIM of any character other than a space has no Cosmos equivalent.");

            // Dispatched on the name: a Java enum's ordinals are not stable across versions and its
            // names are.
            var name = flag.name() switch
            {
                "BOTH" => "TRIM",
                "LEADING" => "LTRIM",
                "TRAILING" => "RTRIM",
                var other => throw new CosmosTranslationException($"TRIM specification '{other}' has no Cosmos equivalent."),
            };

            builder.Append(name).Append('(');
            Write(builder, Operand(call, 2));
            builder.Append(')');
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
