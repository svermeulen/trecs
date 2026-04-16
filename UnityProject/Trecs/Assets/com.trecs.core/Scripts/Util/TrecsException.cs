using System;

namespace Trecs
{
    /// <summary>
    /// Base exception type for errors originating from the Trecs framework.
    /// </summary>
    public class TrecsException : Exception
    {
        public TrecsException(string message)
            : base(message) { }

        public TrecsException(string message, Exception innerE)
            : base(message, innerE) { }
    }
}
