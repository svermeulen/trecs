using System;
using System.Runtime.CompilerServices;
using Unity.Burst;

namespace Trecs.Internal
{
    /// <summary>
    /// Always-on validation helpers — unlike <see cref="TrecsDebugAssert"/>, calls
    /// here run in every build configuration. Use for cheap checks that catch
    /// definite user bugs (null arguments, range violations on user-facing
    /// APIs, public-API misuse). For internal invariants that should compile
    /// out of release builds, prefer <see cref="TrecsDebugAssert"/>.
    ///
    /// Format strings use <c>string.Format</c> placeholders (<c>{0}</c>,
    /// <c>{1}</c>, …). Generic argument overloads avoid boxing on the happy
    /// path. In Burst, the managed throw is stripped via
    /// <see cref="BurstDiscardAttribute"/> and a plain fallback throw fires
    /// instead.
    /// </summary>
    public static class TrecsAssert
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void That(bool condition)
        {
            if (!condition)
            {
                ThrowManaged("Require failed");
                throw new InvalidOperationException("Require failed");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void That(bool condition, string message)
        {
            if (!condition)
            {
                ThrowManaged(message);
                throw new InvalidOperationException(message);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void That<T>(bool condition, string message, T arg1)
        {
            if (!condition)
            {
                ThrowManagedFormatted(message, arg1);
                throw new InvalidOperationException(message);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void That<T1, T2>(bool condition, string message, T1 arg1, T2 arg2)
        {
            if (!condition)
            {
                ThrowManagedFormatted(message, arg1, arg2);
                throw new InvalidOperationException(message);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void That<T1, T2, T3>(
            bool condition,
            string message,
            T1 arg1,
            T2 arg2,
            T3 arg3
        )
        {
            if (!condition)
            {
                ThrowManagedFormatted(message, arg1, arg2, arg3);
                throw new InvalidOperationException(message);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void That<T1, T2, T3, T4>(
            bool condition,
            string message,
            T1 arg1,
            T2 arg2,
            T3 arg3,
            T4 arg4
        )
        {
            if (!condition)
            {
                ThrowManagedFormatted(message, arg1, arg2, arg3, arg4);
                throw new InvalidOperationException(message);
            }
        }

        [BurstDiscard]
        static void ThrowManaged(string message)
        {
            throw CreateException(message);
        }

        [BurstDiscard]
        static void ThrowManagedFormatted<T>(string message, T arg1)
        {
            throw CreateException(message, arg1);
        }

        [BurstDiscard]
        static void ThrowManagedFormatted<T1, T2>(string message, T1 arg1, T2 arg2)
        {
            throw CreateException(message, arg1, arg2);
        }

        [BurstDiscard]
        static void ThrowManagedFormatted<T1, T2, T3>(string message, T1 arg1, T2 arg2, T3 arg3)
        {
            throw CreateException(message, arg1, arg2, arg3);
        }

        [BurstDiscard]
        static void ThrowManagedFormatted<T1, T2, T3, T4>(
            string message,
            T1 arg1,
            T2 arg2,
            T3 arg3,
            T4 arg4
        )
        {
            throw CreateException(message, arg1, arg2, arg3, arg4);
        }

        public static TrecsException CreateException(string message)
        {
            return new TrecsException(message);
        }

        public static TrecsException CreateException<T>(string message, T arg1)
        {
            return new TrecsException(string.Format(message, arg1));
        }

        public static TrecsException CreateException<T1, T2>(string message, T1 arg1, T2 arg2)
        {
            return new TrecsException(string.Format(message, arg1, arg2));
        }

        public static TrecsException CreateException<T1, T2, T3>(
            string message,
            T1 arg1,
            T2 arg2,
            T3 arg3
        )
        {
            return new TrecsException(string.Format(message, arg1, arg2, arg3));
        }

        public static TrecsException CreateException<T1, T2, T3, T4>(
            string message,
            T1 arg1,
            T2 arg2,
            T3 arg3,
            T4 arg4
        )
        {
            return new TrecsException(string.Format(message, arg1, arg2, arg3, arg4));
        }
    }
}
