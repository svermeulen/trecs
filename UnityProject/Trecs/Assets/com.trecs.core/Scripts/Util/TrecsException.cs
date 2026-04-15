using System;

namespace Trecs
{
    public class TrecsException : Exception
    {
        public TrecsException(string message)
            : base(message) { }

        public TrecsException(string message, Exception innerE)
            : base(message, innerE) { }
    }
}
