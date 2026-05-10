using System;
using System.Diagnostics;

namespace Trecs.Samples
{
    /// <summary>
    /// Tiny precondition helper for sample code, kept here so samples don't
    /// have to import Trecs.Internal just to assert invariants. All calls are
    /// compiled out in non-DEBUG builds.
    /// </summary>
    public static class Assert
    {
        [Conditional("DEBUG")]
        public static void That(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException("Sample assertion failed");
        }

        [Conditional("DEBUG")]
        public static void That(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException("Sample assertion failed: " + message);
        }
    }
}
