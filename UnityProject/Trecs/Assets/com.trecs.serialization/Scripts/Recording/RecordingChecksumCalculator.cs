using Trecs.Internal;

namespace Trecs.Serialization
{
    internal class RecordingChecksumCalculator
    {
        readonly WorldStateSerializer _worldStateSerializer;

        public RecordingChecksumCalculator(WorldStateSerializer worldStateSerializer)
        {
            _worldStateSerializer = worldStateSerializer;
        }

        public uint CalculateCurrentChecksum(int version, SerializationBuffer serializerHelper)
        {
            using (TrecsProfiling.Start("CalculateChecksum"))
            {
                serializerHelper.ClearMemoryStream();
                serializerHelper.StartWrite(
                    version: version,
                    // Setting this to false is helpful since it reduces the time for CalculateChecksum from 37 ms to 15 ms
                    // Especially important since we run checksums in QA builds
                    includeTypeChecks: false
                );

                _worldStateSerializer.SerializeState(serializerHelper);
                serializerHelper.EndWrite();

                using (TrecsProfiling.Start("ChecksumCalculator.Run"))
                {
                    var memoryStream = serializerHelper.MemoryStream;

                    memoryStream.Position = 0;

                    byte[] buffer = memoryStream.GetBuffer();
                    int length = (int)memoryStream.Length;

                    return ByteHashCalculator.Run(buffer, length);
                }
            }
        }
    }
}
