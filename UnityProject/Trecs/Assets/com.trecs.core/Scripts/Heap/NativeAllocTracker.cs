using System.Diagnostics;
using System.Threading;

namespace Trecs.Internal
{
    /// <summary>
    /// DEBUG-only process-wide counter of outstanding
    /// <see cref="NativeBlobBox"/> instances. Increments on construction,
    /// decrements on <c>Dispose</c>, and is readable via
    /// <see cref="OutstandingCount"/> for tests that want to snapshot
    /// before+after a scenario and verify no leaks.
    ///
    /// Because the counter is process-wide, callers must snapshot and
    /// compare against the baseline rather than asserting the absolute
    /// value is zero: sibling worlds (multi-world scenarios) and prior
    /// test carry-over both shift the counter independently of any given
    /// operation. Per-heap <c>ClearAll(warnUndisposed: true)</c> is the
    /// primary place leaks are reported; this tracker exists as a
    /// finer-grained probe for targeted tests and debugging sessions.
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
