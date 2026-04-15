using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Burst;

namespace Trecs.Internal
{
    public static class Assert
    {
        [Conditional("DEBUG")]
        public static void That(bool condition)
        {
            if (!condition)
            {
                ThrowManaged();
                throw new InvalidOperationException("Assert hit!");
            }
        }

        [Conditional("DEBUG")]
        public static void That(bool condition, string message)
        {
            if (!condition)
            {
                ThrowManaged(message);
                throw new InvalidOperationException("Assert hit!");
            }
        }

        // Prefer generics to object to avoid boxing and causing allocs
        [Conditional("DEBUG")]
        public static void That<T>(bool condition, string message, T arg1)
        {
            if (!condition)
            {
                ThrowManagedFormatted(message, arg1);
                throw new InvalidOperationException("Assert hit!");
            }
        }

        [Conditional("DEBUG")]
        public static void That<T1, T2>(bool condition, string message, T1 arg1, T2 arg2)
        {
            if (!condition)
            {
                ThrowManagedFormatted(message, arg1, arg2);
                throw new InvalidOperationException("Assert hit!");
            }
        }

        [Conditional("DEBUG")]
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
                throw new InvalidOperationException("Assert hit!");
            }
        }

        [Conditional("DEBUG")]
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
                throw new InvalidOperationException("Assert hit!");
            }
        }

        [Conditional("DEBUG")]
        public static void That<T1, T2, T3, T4, T5>(
            bool condition,
            string message,
            T1 arg1,
            T2 arg2,
            T3 arg3,
            T4 arg4,
            T5 arg5
        )
        {
            if (!condition)
            {
                ThrowManagedFormatted(message, arg1, arg2, arg3, arg4, arg5);
                throw new InvalidOperationException("Assert hit!");
            }
        }

        // Prefer generics to object to avoid boxing and causing allocs
        [Conditional("DEBUG")]
        public static void IsEqual<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected!, actual!))
            {
                ThrowManagedFormatted(
                    "Expected (left): {}, Actual (right): {}",
                    expected!,
                    actual!
                );
                throw new InvalidOperationException(
                    "Assert hit! Expected value is not equal to actual value"
                );
            }
        }

        [Conditional("DEBUG")]
        public static void IsEqual<T>(T expected, T actual, string message)
            where T : notnull
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                ThrowManagedIsEqual(expected, actual, message);
                throw new InvalidOperationException("Assert hit!");
            }
        }

        [Conditional("DEBUG")]
        public static void IsEqual<T1, T2, T3>(
            T1 expected,
            T1 actual,
            string message,
            T2 arg1,
            T3 arg2
        )
            where T1 : notnull
        {
            if (!EqualityComparer<T1>.Default.Equals(expected, actual))
            {
                ThrowManagedIsEqualFormatted(expected, actual, message, arg1, arg2);
                throw new InvalidOperationException("Assert hit!");
            }
        }

        // Prefer generics to object to avoid boxing and causing allocs
        [Conditional("DEBUG")]
        public static void IsNotEqual<T>(T expected, T actual)
            where T : notnull
        {
            if (EqualityComparer<T>.Default.Equals(expected, actual))
            {
                ThrowManagedFormatted("Expected (left): {}, Actual (right): {}", expected, actual);
                throw new InvalidOperationException("Assert hit!");
            }
        }

        [Conditional("DEBUG")]
        public static void IsNotEqual<T>(T expected, T actual, string message)
            where T : notnull
        {
            if (EqualityComparer<T>.Default.Equals(expected, actual))
            {
                ThrowManagedIsEqual(expected, actual, message);
                throw new InvalidOperationException("Assert hit!");
            }
        }

        // Prefer generics to object to avoid boxing and causing allocs
        [Conditional("DEBUG")]
        public static void IsNull<T>(T value)
        {
            if (value != null)
            {
                ThrowManaged("Expected given value to be null");
                throw new InvalidOperationException("Assert hit! Expected given value to be null");
            }
        }

        [Conditional("DEBUG")]
        public static void IsNull<T>(T value, string message)
            where T : class
        {
            if (value != null)
            {
                ThrowManaged(message);
                throw new InvalidOperationException("Assert hit!");
            }
        }

        [Conditional("DEBUG")]
        public static void IsNull<T1, T2>(T1 value, string message, T2 arg1)
            where T1 : class
        {
            if (value != null)
            {
                ThrowManagedFormatted(message, arg1);
                throw new InvalidOperationException("Assert hit!");
            }
        }

        // Prefer generics to object to avoid boxing and causing allocs
        [Conditional("DEBUG")]
        public static void IsNotNull<T>(T value)
            where T : class
        {
            if (value == null)
            {
                ThrowManaged("Expected given value to be non-null");
                throw new InvalidOperationException(
                    "Assert hit! Expected given value to be non-null"
                );
            }
        }

        [Conditional("DEBUG")]
        public static void IsNotNull<T>(T value, string message)
            where T : class
        {
            if (value == null)
            {
                ThrowManaged(message);
                throw new InvalidOperationException("Assert hit!");
            }
        }

        [Conditional("DEBUG")]
        public static void IsNotNull<T1, T2>(T1 value, string message, T2 arg1)
            where T1 : class
        {
            if (value == null)
            {
                ThrowManagedFormatted(message, arg1);
                throw new InvalidOperationException("Assert hit!");
            }
        }

        [Conditional("DEBUG")]
        public static void IsNotNull<T1, T2, T3>(T1 value, string message, T2 arg1, T3 arg2)
            where T1 : class
        {
            if (value == null)
            {
                ThrowManagedFormatted(message, arg1, arg2);
                throw new InvalidOperationException("Assert hit!");
            }
        }

        [Conditional("DEBUG")]
        public static void IsNotNull<T1, T2, T3, T4>(
            T1 value,
            string message,
            T2 arg1,
            T3 arg2,
            T4 arg3
        )
            where T1 : class
        {
            if (value == null)
            {
                ThrowManagedFormatted(message, arg1, arg2, arg3);
                throw new InvalidOperationException("Assert hit!");
            }
        }

        [Conditional("DEBUG")]
        public static void Throws(Action action)
        {
            Throws<Exception>(action);
        }

        [Conditional("DEBUG")]
        public static void Throws<T>(Action action)
            where T : Exception
        {
            try
            {
                action();
            }
            catch (T)
            {
                return;
            }

            throw CreateException(
                "Expected to receive exception of type '{}' but nothing was thrown",
                typeof(T).Name
            );
        }

        // Managed-only throw helpers - stripped from Burst-compiled code.
        // In managed code these throw first (with rich messages), making the
        // fallback throw after them dead code. In Burst, these calls are
        // removed and the fallback throw fires instead.

        [BurstDiscard]
        static void ThrowManaged()
        {
            throw CreateException();
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

        [BurstDiscard]
        static void ThrowManagedFormatted<T1, T2, T3, T4, T5>(
            string message,
            T1 arg1,
            T2 arg2,
            T3 arg3,
            T4 arg4,
            T5 arg5
        )
        {
            throw CreateException(message, arg1, arg2, arg3, arg4, arg5);
        }

        [BurstDiscard]
        static void ThrowManagedIsEqual<T>(T expected, T actual, string message)
            where T : notnull
        {
            throw CreateException(
                "{}\nExpected (left): {}, Actual (right): {}",
                message,
                expected,
                actual
            );
        }

        [BurstDiscard]
        static void ThrowManagedIsEqualFormatted<T1, T2, T3>(
            T1 expected,
            T1 actual,
            string message,
            T2 arg1,
            T3 arg2
        )
            where T1 : notnull
        {
            throw CreateException(
                "{}\nExpected (left): {}, Actual (right): {}",
                string.Format(message, arg1, arg2),
                expected,
                actual
            );
        }

        public static TrecsException CreateException()
        {
            return new TrecsException("Assert hit!");
        }

        public static TrecsException CreateException(string message)
        {
            return new TrecsException("Assert hit! " + message);
        }

        public static TrecsException CreateException<T1>(string message, T1 arg1)
        {
            return new TrecsException("Assert hit! " + CustomFormatter.CustomFormat(message, arg1));
        }

        public static TrecsException CreateException<T1, T2>(string message, T1 arg1, T2 arg2)
        {
            return new TrecsException(
                "Assert hit! " + CustomFormatter.CustomFormat(message, arg1, arg2)
            );
        }

        public static TrecsException CreateException<T1, T2, T3>(
            string message,
            T1 arg1,
            T2 arg2,
            T3 arg3
        )
        {
            return new TrecsException(
                "Assert hit! " + CustomFormatter.CustomFormat(message, arg1, arg2, arg3)
            );
        }

        public static TrecsException CreateException<T1, T2, T3, T4>(
            string message,
            T1 arg1,
            T2 arg2,
            T3 arg3,
            T4 arg4
        )
        {
            return new TrecsException(
                "Assert hit! " + CustomFormatter.CustomFormat(message, arg1, arg2, arg3, arg4)
            );
        }

        public static TrecsException CreateException<T1, T2, T3, T4, T5>(
            string message,
            T1 arg1,
            T2 arg2,
            T3 arg3,
            T4 arg4,
            T5 arg5
        )
        {
            return new TrecsException(
                "Assert hit! " + CustomFormatter.CustomFormat(message, arg1, arg2, arg3, arg4, arg5)
            );
        }

        public static TrecsException CreateException<T1, T2, T3, T4, T5, T6>(
            string message,
            T1 arg1,
            T2 arg2,
            T3 arg3,
            T4 arg4,
            T5 arg5,
            T6 arg6
        )
        {
            return new TrecsException(
                "Assert hit! "
                    + CustomFormatter.CustomFormat(message, arg1, arg2, arg3, arg4, arg5, arg6)
            );
        }
    }
}
