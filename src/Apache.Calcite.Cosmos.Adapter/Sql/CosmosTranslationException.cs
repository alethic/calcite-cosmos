using System;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// Thrown when a relational expression has no Cosmos SQL equivalent.
    /// </summary>
    /// <remarks>
    /// This is a control-flow signal within translation, not an error condition. Conversion rules
    /// catch it and decline to push the operator down, leaving Calcite to evaluate it in-process.
    /// It should never escape to a caller: emitting nothing is always preferable to emitting a
    /// statement Cosmos would reject or, worse, silently misinterpret.
    /// </remarks>
    public class CosmosTranslationException : Exception
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        public CosmosTranslationException() :
            base("The expression has no Cosmos SQL equivalent.")
        {

        }

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="message">A description of what could not be translated.</param>
        public CosmosTranslationException(string message) :
            base(message)
        {

        }

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="message">A description of what could not be translated.</param>
        /// <param name="innerException">The underlying failure.</param>
        public CosmosTranslationException(string message, Exception innerException) :
            base(message, innerException)
        {

        }

    }

}
