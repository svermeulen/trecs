using System;

namespace Trecs.Internal
{
    public class ActionDisposable : IDisposable
    {
        private readonly Action _action;
        private bool _hasDisposed;

        public ActionDisposable(Action action)
        {
            _action = action;
        }

        public void Dispose()
        {
            TrecsAssert.That(!_hasDisposed);
            _hasDisposed = true;
            _action();
        }
    }
}
