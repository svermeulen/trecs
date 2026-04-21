using System.Diagnostics;
using System.Threading;

namespace Trecs.Internal
{
    /// <summary>
    /// DEBUG-only tracker for outstanding native allocations owned by Trecs
    /// heap primitives (e.g. <see cref="NativeBlobBox"/>). Every allocation
    /// path increments the counter; every <c>Dispose</c> decrements it.
    ///
    /// <see cref="World.Dispose"/> asserts the counter is zero so leaks are
    /// surfaced loudly in the test suite rather than relying on a non-main-
    /// thread finalizer log.
    ///
    /// In non-DEBUG builds the inc/dec calls are stripped by
    /// <see cref="ConditionalAttribute"/> and the counter stays at zero.
    /// </summary>
    internal static class NativeAllocTracker
    {
        static int _outstandingCount;

        [Conditional("DEBUG")]
        public static void OnAllocated()
        {
            Interlocked.Increment(ref _outstandingCount);
        }

        [Conditional("DEBUG")]
        public static void OnDisposed()
        {
            Interlocked.Decrement(ref _outstandingCount);
        }

        public static int OutstandingCount => Volatile.Read(ref _outstandingCount);
    }
}
