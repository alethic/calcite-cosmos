using Apache.Calcite.Cosmos.Adapter.Sql;

using java.util.function;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Converts a <see cref="Project"/> to a <see cref="CosmosProject"/>.
    /// </summary>
    public class CosmosProjectRule : CosmosConverterRule
    {

        /// <summary>
        /// Determines whether every projected expression can be rendered as Cosmos SQL.
        /// </summary>
        /// <remarks>
        /// Checked against the binding derived from the input row type, which is the same binding
        /// implementation will use, so a projection accepted here will not fail later.
        /// </remarks>
        static bool IsTranslatable(Project project)
        {
            var projects = project.getProjects();
            if (projects.size() == 0)
                return false;

            var fields = CosmosImplementor.BindFields(project.getInput().getRowType());
            var translator = new CosmosRexTranslator(project.getCluster().getRexBuilder(), fields, new CosmosParameterList());

            for (var i = 0; i < projects.size(); i++)
                if (translator.TryTranslate((RexNode)projects.get(i), out _) == false)
                    return false;

            // Window functions have no Cosmos equivalent.
            return project.containsOver() == false;
        }

        /// <summary>
        /// Creates a rule instance bound to the specified convention.
        /// </summary>
        /// <param name="convention">The Cosmos convention this rule targets.</param>
        /// <returns>A configured rule.</returns>
        public static CosmosProjectRule Create(CosmosConvention convention)
        {
            return (CosmosProjectRule)Config.INSTANCE
                .withConversion(typeof(Project), new DelegatePredicate<Project>(IsTranslatable), Convention.NONE, convention, "CosmosProjectRule")
                .withRuleFactory(new DelegateFunction<Config, CosmosProjectRule>(c => new CosmosProjectRule(c)))
                .toRule(typeof(CosmosProjectRule));
        }

        /// <summary>
        /// Initializes a new instance using the supplied rule configuration.
        /// </summary>
        /// <param name="config">The rule configuration produced by <see cref="Create"/>.</param>
        public CosmosProjectRule(Config config) :
            base(config)
        {

        }

        /// <inheritdoc />
        public override bool matches(RelOptRuleCall call)
        {
            // A correlated projection references a variable bound outside this statement, which
            // Cosmos cannot express here.
            return ((Project)call.rel(0)).getVariablesSet().isEmpty();
        }

        /// <inheritdoc />
        public override RelNode convert(RelNode rel)
        {
            var project = (Project)rel;

            return new CosmosProject(
                project.getCluster(),
                project.getTraitSet().replace(@out),
                convert(project.getInput(), project.getInput().getTraitSet().replace(@out)),
                project.getProjects(),
                project.getRowType());
        }

    }

}
