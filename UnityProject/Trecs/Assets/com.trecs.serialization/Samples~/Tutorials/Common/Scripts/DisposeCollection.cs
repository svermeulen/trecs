using System;
using System.Collections.Generic;
using Trecs.Internal;

namespace Trecs.Serialization.Samples
{
    public static class DisposeGroupExtensions
    {
        public static T AddTo<T>(this T disposable, DisposeCollection group)
            where T : IDisposable
        {
            return group.Add(disposable);
        }
    }

    public class DisposeCollection : IDisposable
    {
        private readonly List<IDisposable> _disposables = new();
        private bool _hasDisposed;

        public bool IsEmpty
        {
            get { return _disposables.Count == 0; }
        }

        public void Dispose()
        {
            Assert.That(!_hasDisposed);
            _hasDisposed = true;

            // Dispose in reverse order from when they were added
            // Ideally shouldn't matter but more likely to make more
            // sense this way
            for (int i = _disposables.Count - 1; i >= 0; i--)
            {
                _disposables[i].Dispose();
            }

            _disposables.Clear();
        }

        public T Add<T>(T disposable)
            where T : IDisposable
        {
            Assert.That(!_hasDisposed);
            Assert.That(!_disposables.Contains(disposable), "Disposable added multiple times");

            _disposables.Add(disposable);
            return disposable;
        }
    }
}
