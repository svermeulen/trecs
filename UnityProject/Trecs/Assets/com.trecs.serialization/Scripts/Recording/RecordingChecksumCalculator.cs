using System.ComponentModel;
using Trecs.Internal;

namespace Trecs.Serialization.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class RecordingChecksumCalculator
    {
        readonly IWorldStateSerializer _worldStateSerializer;

        public RecordingChecksumCalculator(IWorldStateSerializer worldStateSerializer)
        {
            _worldStateSerializer = worldStateSerializer;
        }

        public uint CalculateCurrentChecksum(
            int version,
            SerializationBuffer serializerHelper,
            long flags = 0
        )
        {
            using (TrecsProfiling.Start("CalculateChecksum"))
            {
                serializerHelper.ClearMemoryStream();
                serializerHelper.StartWrite(
                    version: version,
                    // Setting this to false is helpful since it reduces the time for CalculateChecksum from 37 ms to 15 ms
                    // Especially important since we run checksums in QA builds
                    includeTypeChecks: false,
                    flags: flags
                );

                _worldStateSerializer.SerializeState(serializerHelper);
                serializerHelper.EndWrite();

                using (TrecsProfiling.Start("ChecksumCalculator.Run"))
                {
                    // Use the buffer helper rather than reaching into MemoryStream
                    // directly: GetBuffer() returns the whole internal array and it
                    // is easy to hash uninitialized trailing bytes by mistake, which
                    // would poison the checksum and break replay / desync detection.
                    return serializerHelper.ComputeChecksum();
                }
            }
        }
    }
}
