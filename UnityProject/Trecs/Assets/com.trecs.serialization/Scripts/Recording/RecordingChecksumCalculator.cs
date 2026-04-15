using Trecs.Collections;
using Trecs.Internal;

namespace Trecs.Serialization
{
    public class RecordingChecksumCalculator
    {
        static readonly TrecsLog _log = new(nameof(RecordingChecksumCalculator));

        readonly IGameStateSerializer _gameStateSerializer;

        public RecordingChecksumCalculator(IGameStateSerializer gameStateSerializer)
        {
            _gameStateSerializer = gameStateSerializer;
        }

        public uint CalculateCurrentChecksum(
            int version,
            SerializationBuffer serializerHelper,
            ReadOnlyDenseHashSet<int> flags
        )
        {
            using (TrecsProfiling.Start("CalculateChecksum"))
            {
                // Note that this includes static seed here
                // which probably is also helpful to include in checksum,
                // in case that changes at runtime
                _gameStateSerializer.StartSerialize(
                    version: version,
                    serializerHelper,
                    flags,
                    // Setting this to false is helpful since it reduces the time for CalculateChecksum from 37 ms to 15 ms
                    // Especially important since we run checksums in QA builds
                    includeTypeChecks: false
                );

                _gameStateSerializer.SerializeCurrentState(serializerHelper);
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
