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

        // Generic overloads so the format args aren't evaluated/boxed unless
        // the assert actually fires.
        [Conditional("DEBUG")]
        public static void That<T>(bool condition, string message, T arg0)
        {
            if (!condition)
                throw new InvalidOperationException(
                    "Sample assertion failed: " + string.Format(message, arg0)
                );
        }

        [Conditional("DEBUG")]
        public static void That<T0, T1>(bool condition, string message, T0 arg0, T1 arg1)
        {
            if (!condition)
                throw new InvalidOperationException(
                    "Sample assertion failed: " + string.Format(message, arg0, arg1)
                );
        }

        [Conditional("DEBUG")]
        public static void That<T0, T1, T2>(
            bool condition,
            string message,
            T0 arg0,
            T1 arg1,
            T2 arg2
        )
        {
            if (!condition)
                throw new InvalidOperationException(
                    "Sample assertion failed: " + string.Format(message, arg0, arg1, arg2)
                );
        }
    }
}
