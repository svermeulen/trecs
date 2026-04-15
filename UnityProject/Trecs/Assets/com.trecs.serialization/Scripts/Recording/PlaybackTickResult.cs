namespace Trecs.Serialization
{
    public struct PlaybackTickResult
    {
        public bool ChecksumVerified;
        public bool DesyncDetected;
        public uint? ExpectedChecksum;
        public uint? ActualChecksum;
    }
}
