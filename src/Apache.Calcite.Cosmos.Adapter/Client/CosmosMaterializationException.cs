using System;

namespace Apache.Calcite.Cosmos.Adapter.Client
{

    /// <summary>
    /// Thrown when a value Cosmos returned cannot be read as the type the plan was built against.
    /// </summary>
    /// <remarks>
    /// The counterpart of <see cref="Sql.CosmosTranslationException"/> at the other end of the query, and
    /// the opposite kind of signal: a translation failure is caught by a rule and costs only a pushdown,
    /// whereas this reaches the caller. Nothing can be done about it here — the row has already been
    /// fetched and the plan already compiled — and it means what it says, that a document does not hold
    /// what the query assumed. A container has no row schema to have prevented that.
    /// </remarks>
    public class CosmosMaterializationException : Exception
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="message">A description of what could not be read.</param>
        public CosmosMaterializationException(string message) :
            base(message)
        {

        }

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="message">A description of what could not be read.</param>
        /// <param name="innerException">The underlying failure.</param>
        public CosmosMaterializationException(string message, Exception innerException) :
            base(message, innerException)
        {

        }

    }

}
