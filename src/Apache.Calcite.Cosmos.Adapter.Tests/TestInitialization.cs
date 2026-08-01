using System.Runtime.CompilerServices;

namespace Apache.Calcite.Cosmos.Adapter.Tests
{

    /// <summary>
    /// Process-wide setup for the test assembly.
    /// </summary>
    public static class TestInitialization
    {

        /// <summary>
        /// Publishes <c>calcite.core</c> into the boot class loader.
        /// </summary>
        /// <remarks>
        /// <para>
        /// IKVM gives each assembly its own class loader, whereas a JVM has one flat classpath.
        /// Avatica's <c>UnregisteredDriver</c> resolves the Calcite JDBC factory with
        /// <c>Class.forName("org.apache.calcite.jdbc.CalciteJdbc41Factory")</c>, which binds
        /// against the calling class's loader — <c>avatica.core</c>. That factory lives in
        /// <c>calcite.core</c>, and avatica does not reference calcite: the dependency runs the
        /// other way. The class is present and loadable, but not by the loader doing the lookup,
        /// so the driver's static initializer fails with <c>ClassNotFoundException</c> and takes
        /// every Calcite entry point that opens an internal connection down with it —
        /// <c>Frameworks.getPlanner</c> and <c>RelBuilder.create</c> among them.
        /// </para>
        /// <para>
        /// Adding the assembly to the boot class path makes its classes visible to
        /// <c>Class.forName</c> from anywhere, which restores the flat-classpath assumption the
        /// Java code was written against.
        /// </para>
        /// <para>
        /// A module initializer rather than <c>[AssemblyInitialize]</c>: the addition has to
        /// precede the first load of the driver, and a type initializer runs once with its failure
        /// cached for the life of the process. Test-framework hooks do not reliably run early
        /// enough.
        /// </para>
        /// </remarks>
        [ModuleInitializer]
        internal static void AddCalciteToBootClassPath()
        {
            ikvm.runtime.Startup.addBootClassPathAssembly(typeof(org.apache.calcite.jdbc.CalciteFactory).Assembly);
        }

    }

}
