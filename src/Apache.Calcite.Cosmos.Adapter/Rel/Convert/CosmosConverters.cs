using System;
using System.Linq.Expressions;
using System.Text.Json;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Sql;

using Apache.Calcite.Extensions.Adapter.Enumerable;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.type;
using org.apache.calcite.rex;
using org.apache.calcite.sql.type;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// What the two converters out of <see cref="CosmosConvention"/> have in common.
    /// </summary>
    /// <remarks>
    /// The conventions differ only in whether the sequence is awaited. The statement, the partition key,
    /// the row shape and the row builder are the same either way, so they are decided here once.
    /// </remarks>
    static class CosmosConverters
    {

        static readonly System.Reflection.MethodInfo GetPropertyMethod = typeof(CosmosJson).GetMethod(nameof(CosmosJson.GetProperty), [typeof(JsonElement), typeof(string), typeof(SqlTypeName)])
            ?? throw new InvalidOperationException($"'{nameof(CosmosJson.GetProperty)}' is missing from {nameof(CosmosJson)}.");

        static readonly System.Reflection.MethodInfo GetExecutorMethod = typeof(CosmosSchemas).GetMethod(nameof(CosmosSchemas.GetExecutor), [typeof(org.apache.calcite.DataContext), typeof(string[])])
            ?? throw new InvalidOperationException($"'{nameof(CosmosSchemas.GetExecutor)}' is missing from {nameof(CosmosSchemas)}.");

        /// <summary>
        /// Renders the statement a subtree of Cosmos nodes stands for.
        /// </summary>
        /// <param name="input">The subtree, whose root is the node being converted.</param>
        /// <param name="rexBuilder">Used when translating expressions.</param>
        /// <returns>The statement, its bound parameters, and any partition key a filter pinned.</returns>
        /// <exception cref="CosmosTranslationException">The subtree has no Cosmos SQL equivalent.</exception>
        public static CosmosQuery GenerateQuery(RelNode input, RexBuilder rexBuilder)
        {
            if (input.getConvention() is not CosmosConvention convention)
                throw new CosmosTranslationException($"Node '{input.getRelTypeName()}' is not in the Cosmos convention.");

            var implementor = new CosmosImplementor(rexBuilder, convention.Container);
            implementor.Visit(input);

            EnsureProjection(implementor, input.getRowType());

            return implementor.Build();
        }

        /// <summary>
        /// Projects the subtree's output fields by name where nothing above the scan already has.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Without this a bare scan renders <c>SELECT VALUE c</c> and the row arrives as the document
        /// itself, while a projected query renders <c>SELECT VALUE { … }</c> and the row arrives as an
        /// object keyed by output field name. Those are two row shapes, and the materializer would need to
        /// know which it was given.
        /// </para>
        /// <para>
        /// Emitting the object constructor in both cases collapses that to one shape: a row is always an
        /// object whose properties are the output fields. The paths it projects are the bindings the scan
        /// established, so <c>_MAP</c> projects the document and a promoted column projects its property —
        /// the same values the other shape would have carried.
        /// </para>
        /// </remarks>
        static void EnsureProjection(CosmosImplementor implementor, RelDataType rowType)
        {
            if (implementor.Query.HasProjection)
                return;

            var fields = rowType.getFieldList();
            var paths = implementor.Fields;

            // Reaching here with fewer paths than fields, or with an unbound one, would silently project
            // the wrong values. Nothing that leaves a field unbound can reach this — a computed
            // projection is a projection, and this runs only when there is none — but the result of
            // getting it wrong is a wrong answer rather than a failure, so it is checked.
            if (paths.Count < fields.size())
                throw new CosmosTranslationException("The output fields are not bound to document paths, so the result cannot be projected.");

            for (var i = 0; i < fields.size(); i++)
            {
                var path = paths[i] ?? throw new CosmosTranslationException($"Output field '{((RelDataTypeField)fields.get(i)).getName()}' is not bound to a document path.");
                implementor.Query.SelectProperty(((RelDataTypeField)fields.get(i)).getName(), path.ToString());
            }
        }

        /// <summary>
        /// Returns the expression by which the running plan reaches what executes its statement.
        /// </summary>
        /// <remarks>
        /// The table's qualified name is a planning-time fact and is written into the plan; the executor
        /// is not, and is looked up through the <see cref="org.apache.calcite.DataContext"/> every time the
        /// plan runs.
        /// </remarks>
        /// <param name="input">The subtree being converted.</param>
        /// <param name="root">The parameter the context arrives by.</param>
        /// <returns>The expression.</returns>
        public static Expression ExecutorExpression(RelNode input, ParameterExpression root)
        {
            var scan = FindScan(input) ?? throw new CosmosTranslationException("The subtree has no Cosmos table scan at its leaf.");
            var qualifiedName = scan.getTable().getQualifiedName();

            var names = new string[qualifiedName.size()];
            for (var i = 0; i < names.Length; i++)
                names[i] = (string)qualifiedName.get(i);

            return Expression.Call(null, GetExecutorMethod, root, Expression.Constant(names));
        }

        /// <summary>
        /// Returns the single scan a Cosmos subtree bottoms out at.
        /// </summary>
        /// <remarks>
        /// There is always exactly one. A convention instance is bound to one container, and Cosmos has
        /// neither relational joins nor set operators, so nothing can bring a second scan into the same
        /// subtree.
        /// </remarks>
        static CosmosTableScan? FindScan(RelNode node)
        {
            if (node is CosmosTableScan scan)
                return scan;

            var inputs = node.getInputs();
            for (var i = 0; i < inputs.size(); i++)
                if (FindScan((RelNode)inputs.get(i)) is CosmosTableScan found)
                    return found;

            return null;
        }

        /// <summary>
        /// Builds the delegate that reads one row from the JSON value Cosmos returned for it.
        /// </summary>
        /// <remarks>
        /// The row's shape follows from its arity exactly as it does in Calcite's own converters, because
        /// <c>JavaRowFormat.optimize</c> has already told the physical type the same thing: one field is
        /// the value itself, and only beyond that is a row an array.
        /// </remarks>
        /// <param name="physType">The physical type of the rows.</param>
        /// <param name="rowType">The logical row type, whose field names key the JSON object.</param>
        /// <returns>The lambda.</returns>
        public static LambdaExpression RowBuilder(ClrPhysType physType, RelDataType rowType)
        {
            var row = Expression.Parameter(typeof(JsonElement), "row");
            var fields = rowType.getFieldList();
            var fieldCount = fields.size();

            Expression body;
            if (fieldCount == 0)
                body = Expression.Constant(null, typeof(object));
            else if (fieldCount == 1)
                body = ReadField(row, (RelDataTypeField)fields.get(0));
            else
            {
                var values = new Expression[fieldCount];
                for (var i = 0; i < fieldCount; i++)
                    values[i] = ReadField(row, (RelDataTypeField)fields.get(i));

                body = Expression.NewArrayInit(typeof(object), values);
            }

            // The reader hands back the value already boxed as Calcite holds it, so what is left is the
            // conversion this row shape needs: a cast where the row is its single column, and nothing at
            // all where it is the object[] every wider row is.
            if (body.Type != physType.RowType)
                body = Expression.Convert(body, physType.RowType);

            return Expression.Lambda(typeof(Func<,>).MakeGenericType(typeof(JsonElement), physType.RowType), body, row);
        }

        /// <summary>
        /// Returns the expression reading one output field out of a row.
        /// </summary>
        /// <remarks>
        /// Read by name rather than by position. A Cosmos object constructor does not emit a property whose
        /// value is undefined, so the properties present in a row are a subset of the output fields and
        /// their positions are not the fields' positions.
        /// </remarks>
        static Expression ReadField(ParameterExpression row, RelDataTypeField field)
        {
            return Expression.Call(null,
                GetPropertyMethod,
                row,
                Expression.Constant(field.getName()),
                Expression.Constant(field.getType().getSqlTypeName()));
        }

    }

}
