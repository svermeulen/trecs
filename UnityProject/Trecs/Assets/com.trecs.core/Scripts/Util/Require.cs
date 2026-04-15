using System;
using System.Runtime.CompilerServices;
using Unity.Burst;

namespace Trecs.Internal
{
    /// <summary>
    /// Always-on assertions that throw TrecsException even in release builds.
    /// Use for cheap checks that catch definite user bugs (read-only violations,
    /// null refs, bounds checks on user-facing APIs).
    ///
    /// Uses generic args and {} format strings to avoid boxing and string
    /// interpolation on the happy path. In Burst, the managed throw is stripped
    /// via [BurstDiscard] and a plain fallback throw fires instead.
    /// </summary>
    public static class Require
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
                throw new InvalidOperationException("Require failed");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void That<T>(bool condition, string message, T arg1)
        {
            if (!condition)
            {
                ThrowManagedFormatted(message, arg1);
                throw new InvalidOperationException("Require failed");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void That<T1, T2>(bool condition, string message, T1 arg1, T2 arg2)
        {
            if (!condition)
            {
                ThrowManagedFormatted(message, arg1, arg2);
                throw new InvalidOperationException("Require failed");
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
                throw new InvalidOperationException("Require failed");
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
                throw new InvalidOperationException("Require failed");
            }
        }

        [BurstDiscard]
        static void ThrowManaged(string message)
        {
            throw new TrecsException(message);
        }

        [BurstDiscard]
        static void ThrowManagedFormatted<T>(string message, T arg1)
        {
            throw new TrecsException(CustomFormatter.CustomFormat(message, arg1));
        }

        [BurstDiscard]
        static void ThrowManagedFormatted<T1, T2>(string message, T1 arg1, T2 arg2)
        {
            throw new TrecsException(CustomFormatter.CustomFormat(message, arg1, arg2));
        }

        [BurstDiscard]
        static void ThrowManagedFormatted<T1, T2, T3>(string message, T1 arg1, T2 arg2, T3 arg3)
        {
            throw new TrecsException(CustomFormatter.CustomFormat(message, arg1, arg2, arg3));
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
            throw new TrecsException(CustomFormatter.CustomFormat(message, arg1, arg2, arg3, arg4));
        }
    }
}
