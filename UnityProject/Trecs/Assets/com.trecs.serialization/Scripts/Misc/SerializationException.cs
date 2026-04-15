using System;

namespace Trecs.Serialization
{
    public class SerializationException : Exception
    {
        public SerializationException(string message)
            : base(message) { }

        public SerializationException(string message, Exception innerException)
            : base(message, innerException) { }
    }
}
