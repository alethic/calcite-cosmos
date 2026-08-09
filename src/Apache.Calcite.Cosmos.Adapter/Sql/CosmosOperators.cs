using org.apache.calcite.sql;
using org.apache.calcite.sql.type;
using org.apache.calcite.sql.util;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// The Cosmos functions that have no counterpart in SQL, and the operator table that makes them
    /// nameable in a query.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Full text search is not SQL. Calcite's standard operator table has nothing to map onto
    /// <c>FULLTEXTCONTAINS</c>, so a query cannot name it unless the operator exists — which is what
    /// these are. Chain <see cref="Instance"/> into the operator table the validator is built with:
    /// </para>
    /// <code>
    /// SqlOperatorTables.chain(SqlStdOperatorTable.instance(), CosmosOperators.Instance)
    /// </code>
    /// <para>
    /// The signatures are the service's, taken from the query language reference. The first argument of
    /// every one of them is a <em>property path</em> rather than an arbitrary expression, and the
    /// translator holds them to that: a call over anything it cannot resolve to a path is declined.
    /// </para>
    /// <para>
    /// The scoring functions are here too, and are a different kind of thing: <c>FULLTEXTSCORE</c> and
    /// <c>RRF</c> are legal only in an <c>ORDER BY RANK</c> clause and may not be projected. They exist
    /// as operators so that a query can write <c>ORDER BY FULLTEXTSCORE(…)</c>, which is the only shape
    /// <c>CosmosRankRule</c> recognises; the translator refuses them anywhere else.
    /// </para>
    /// </remarks>
    public static class CosmosOperators
    {

        /// <summary>
        /// <c>FULLTEXTCONTAINS(&lt;property_path&gt;, &lt;string_expr&gt;)</c> — whether the keyword
        /// occurs in the property.
        /// </summary>
        public static readonly SqlFunction FullTextContains = Predicate("FULLTEXTCONTAINS", 2, 2);

        /// <summary>
        /// <c>FULLTEXTCONTAINSALL(&lt;property_path&gt;, &lt;string_expr1&gt;, …)</c> — whether every
        /// keyword occurs in the property.
        /// </summary>
        public static readonly SqlFunction FullTextContainsAll = Predicate("FULLTEXTCONTAINSALL", 2, -1);

        /// <summary>
        /// <c>FULLTEXTCONTAINSANY(&lt;property_path&gt;, &lt;string_expr1&gt;, …)</c> — whether any
        /// keyword occurs in the property.
        /// </summary>
        public static readonly SqlFunction FullTextContainsAny = Predicate("FULLTEXTCONTAINSANY", 2, -1);

        /// <summary>
        /// <c>FULLTEXTSCORE(&lt;property_path&gt;, &lt;string_expr1&gt;, …)</c> — a BM25 relevance score.
        /// </summary>
        /// <remarks>
        /// Legal only in an <c>ORDER BY RANK</c> clause or as an argument to <see cref="Rrf"/>, and
        /// explicitly not in a projection. It exists as an operator so a query can write
        /// <c>ORDER BY FULLTEXTSCORE(…)</c>; <c>CosmosRankRule</c> is what recognises that shape and
        /// turns it into the clause. The translator refuses it everywhere else, so a plan that reached
        /// a <c>WHERE</c> or a select list with one declines rather than emitting a rejected statement.
        /// </remarks>
        public static readonly SqlFunction FullTextScore = Scoring("FULLTEXTSCORE", 2);

        /// <summary>
        /// <c>RRF(&lt;function1&gt;, &lt;function2&gt;, …, &lt;weights&gt;)</c> — a fused score.
        /// </summary>
        /// <remarks>
        /// Combines two or more scoring functions, optionally weighted by a trailing array. Subject to
        /// the same restriction as <see cref="FullTextScore"/>, and additionally cannot be combined with
        /// ordering on other property paths.
        /// </remarks>
        public static readonly SqlFunction Rrf = Scoring("RRF", 2);

        /// <summary>
        /// <c>IS_DEFINED(&lt;expr&gt;)</c> — whether the property exists on the document at all.
        /// </summary>
        /// <remarks>
        /// The one type test with no SQL counterpart even in spirit. SQL has no <c>undefined</c>: a
        /// column is null or it is not, whereas a Cosmos property may be absent, and only this
        /// distinguishes absent from present-and-null. <c>IS NULL</c> already renders as both together,
        /// so this exists for a query that needs to tell them apart.
        /// </remarks>
        public static readonly SqlFunction IsDefined = TypeTest("IS_DEFINED");

        /// <summary><c>IS_ARRAY(&lt;expr&gt;)</c>.</summary>
        public static readonly SqlFunction IsArray = TypeTest("IS_ARRAY");

        /// <summary><c>IS_BOOL(&lt;expr&gt;)</c>.</summary>
        public static readonly SqlFunction IsBool = TypeTest("IS_BOOL");

        /// <summary><c>IS_NULL(&lt;expr&gt;)</c> — present, and holding JSON null.</summary>
        public static readonly SqlFunction IsNull = TypeTest("IS_NULL");

        /// <summary><c>IS_NUMBER(&lt;expr&gt;)</c>.</summary>
        public static readonly SqlFunction IsNumber = TypeTest("IS_NUMBER");

        /// <summary><c>IS_OBJECT(&lt;expr&gt;)</c>.</summary>
        public static readonly SqlFunction IsObject = TypeTest("IS_OBJECT");

        /// <summary><c>IS_PRIMITIVE(&lt;expr&gt;)</c> — a string, number, boolean or null.</summary>
        public static readonly SqlFunction IsPrimitive = TypeTest("IS_PRIMITIVE");

        /// <summary><c>IS_STRING(&lt;expr&gt;)</c>.</summary>
        public static readonly SqlFunction IsString = TypeTest("IS_STRING");

        /// <summary>
        /// Gets an operator table carrying every Cosmos-specific function.
        /// </summary>
        public static SqlOperatorTable Instance { get; } = SqlOperatorTables.of(
            [
                FullTextContains, FullTextContainsAll, FullTextContainsAny,
                FullTextScore, Rrf,
                IsDefined, IsArray, IsBool, IsNull, IsNumber, IsObject, IsPrimitive, IsString,
            ]);

        /// <summary>
        /// Defines a scoring function, whose value ranks a row rather than describing it.
        /// </summary>
        /// <remarks>
        /// Typed as a double so that a query can name it in an <c>ORDER BY</c> and the validator will
        /// accept the sort. Nothing may read the value — Cosmos will not project one — and the
        /// translator enforces that; the type is what makes the clause expressible, not a promise that
        /// a number comes back.
        /// </remarks>
        static SqlFunction Scoring(string name, int min)
        {
            return SqlBasicFunction.create(
                name,
                ReturnTypes.DOUBLE,
                OperandTypes.variadic(SqlOperandCountRanges.from(min)),
                SqlFunctionCategory.SYSTEM);
        }

        /// <summary>
        /// Defines a unary type test.
        /// </summary>
        /// <remarks>
        /// A container has no row schema, so what a property holds is a question about the document
        /// rather than about the table, and only the service can answer it. These are the functions that
        /// ask. Unlike the full text predicates the argument is an ordinary expression, not a path —
        /// <c>IS_NUMBER(c.a + c.b)</c> is meaningful — so nothing further is required of it.
        /// </remarks>
        static SqlFunction TypeTest(string name)
        {
            return SqlBasicFunction.create(
                name,
                ReturnTypes.BOOLEAN_NOT_NULL,
                OperandTypes.ANY,
                SqlFunctionCategory.SYSTEM);
        }

        /// <summary>
        /// Defines a boolean full text predicate over a property path and one or more keywords.
        /// </summary>
        /// <remarks>
        /// The operand types are left open. What the first argument has to be is a path, which is a
        /// question about the <em>expression</em> and not about its type, so the validator cannot ask it
        /// and the translator does.
        /// </remarks>
        static SqlFunction Predicate(string name, int min, int max)
        {
            var range = max < 0 ? SqlOperandCountRanges.from(min) : SqlOperandCountRanges.between(min, max);

            return SqlBasicFunction.create(
                name,
                ReturnTypes.BOOLEAN_NULLABLE,
                OperandTypes.variadic(range),
                SqlFunctionCategory.SYSTEM);
        }

    }

}
