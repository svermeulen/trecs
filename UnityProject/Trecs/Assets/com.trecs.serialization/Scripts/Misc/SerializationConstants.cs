namespace Trecs.Serialization
{
    public class SerializationConstants
    {
        // Sentinel value written at end of serialization to detect stream corruption
        public const byte SentinelValue = 0x5E;
    }
}
