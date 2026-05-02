namespace Trecs.Serialization
{
    public readonly struct AutoRecordingBookmark
    {
        public readonly int Frame;
        public readonly byte[] Data;

        // Checksum of the world state at this frame, computed via
        // IWorldStateSerializer with IsForChecksum so non-deterministic fields
        // (Unity GUIDs, transient handles) don't poison comparisons. Used to
        // detect desyncs when the simulation is re-run from an earlier
        // bookmark and crosses this frame again.
        public readonly uint Checksum;

        public AutoRecordingBookmark(int frame, byte[] data, uint checksum)
        {
            Frame = frame;
            Data = data;
            Checksum = checksum;
        }
    }
}
