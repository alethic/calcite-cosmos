using System;

namespace Apache.Calcite.Cosmos.Adapter.Client
{

    /// <summary>
    /// Thrown when a compiled plan cannot reach what it needs to execute its statement.
    /// </summary>
    /// <remarks>
    /// A plan holds its table's qualified name rather than the table, and resolves it against the schema
    /// it is executing against. This is what that resolution failing looks like: the schema is not the one
    /// the plan was built against, or the table it names was never given a Cosmos client to read through.
    /// Neither is recoverable at the point it is discovered, both being settled long before the query ran.
    /// </remarks>
    public class CosmosExecutionException : Exception
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="message">A description of what could not be reached.</param>
        public CosmosExecutionException(string message) :
            base(message)
        {

        }

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="message">A description of what could not be reached.</param>
        /// <param name="innerException">The underlying failure.</param>
        public CosmosExecutionException(string message, Exception innerException) :
            base(message, innerException)
        {

        }

    }

}
