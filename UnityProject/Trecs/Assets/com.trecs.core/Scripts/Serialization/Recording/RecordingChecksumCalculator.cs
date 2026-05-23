using System.ComponentModel;

namespace Trecs.Internal
{
    /// <summary>
    /// Compute a 64-bit xxHash of the world's current deterministic state.
    /// Stateless — pass in the world-state serializer to use and a reusable
    /// <see cref="SerializationBuffer"/> for the framing. The buffer is
    /// reset before writing so callers can share one across capture sites.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class RecordingChecksumCalculator
    {
        public static ulong Calculate(
            IWorldStateSerializer worldStateSerializer,
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

                worldStateSerializer.SerializeForChecksum(serializerHelper.Writer);
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
