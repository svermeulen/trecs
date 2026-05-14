using System;

namespace Trecs.Internal
{
    public class NullDisposable : IDisposable
    {
        private NullDisposable() { }

        public void Dispose() { }

        public static readonly NullDisposable Instance = new();
    }
}
