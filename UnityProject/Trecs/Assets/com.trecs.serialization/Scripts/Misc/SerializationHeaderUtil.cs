using System.IO;

namespace Trecs.Internal
{
    internal static class SerializationHeaderUtil
    {
        public static (int version, bool preferTypeChecks) ReadHeader(BinaryReader reader)
        {
            var version = reader.ReadInt32();
            var includesTypeChecks = reader.ReadBoolean();

            return (version, includesTypeChecks);
        }
    }
}
