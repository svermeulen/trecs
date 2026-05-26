using System.Diagnostics;
using System.Globalization;

namespace Trecs
{
    static class SerializationWriterScopeExtensions
    {
        [Conditional("DEBUG")]
        public static void PushScope(this ISerializationWriter writer, string name)
        {
#if DEBUG
            if (writer is FlatPathSerializationWriter fpw)
                fpw.PushScope(name);
#endif
        }

        [Conditional("DEBUG")]
        public static void PushScope<T0>(this ISerializationWriter writer, string format, T0 arg0)
        {
#if DEBUG
            if (writer is FlatPathSerializationWriter fpw)
                fpw.PushScope(string.Format(CultureInfo.InvariantCulture, format, arg0));
#endif
        }

        [Conditional("DEBUG")]
        public static void PushScope<T0, T1>(
            this ISerializationWriter writer,
            string format,
            T0 arg0,
            T1 arg1
        )
        {
#if DEBUG
            if (writer is FlatPathSerializationWriter fpw)
                fpw.PushScope(string.Format(CultureInfo.InvariantCulture, format, arg0, arg1));
#endif
        }

        [Conditional("DEBUG")]
        public static void PopScope(this ISerializationWriter writer)
        {
#if DEBUG
            if (writer is FlatPathSerializationWriter fpw)
                fpw.PopScope();
#endif
        }
    }
}
