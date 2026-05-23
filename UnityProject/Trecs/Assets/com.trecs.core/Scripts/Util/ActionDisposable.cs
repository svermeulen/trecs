using System;

namespace Trecs.Internal
{
    internal class ActionDisposable : IDisposable
    {
        private readonly Action _action;
        private bool _hasDisposed;

        public ActionDisposable(Action action)
        {
            _action = action;
        }

        public void Dispose()
        {
            TrecsDebugAssert.That(!_hasDisposed);
            _hasDisposed = true;
            _action();
        }
    }
}
