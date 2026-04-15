using System;

namespace Trecs.SourceGen.Internal
{
    internal class AssertException(string message) : Exception(message) { }

    internal static class Assert
    {
        public static void That(bool condition)
        {
            if (!condition)
            {
                throw CreateException();
            }
        }

        public static void That(bool condition, string message)
        {
            if (!condition)
            {
                throw CreateException(message);
            }
        }

        public static void IsNotNull<T>(T value)
            where T : class
        {
            if (value == null)
            {
                throw CreateException("Expected given value to be non-null");
            }
        }

        public static AssertException CreateException()
        {
            return new AssertException("Assert hit!");
        }

        public static AssertException CreateException(string message)
        {
            return new AssertException("Assert hit! " + message);
        }
    }
}
