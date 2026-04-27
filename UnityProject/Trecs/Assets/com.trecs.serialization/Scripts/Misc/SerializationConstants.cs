namespace Trecs.Serialization
{
    public static class SerializationConstants
    {
        /// <summary>
        /// Marker byte written at the end of every Trecs binary payload by
        /// <c>BinarySerializationWriter</c> and verified on read. Catches
        /// truncated streams and a narrow class of corruption. This is the
        /// payload-level marker — not to be confused with
        /// <c>WorldStateSerializer.WorldStateStreamGuard</c>, which is an
        /// internal int guarding the ECS-state section *inside* the payload.
        /// </summary>
        public const byte EndOfPayloadMarker = 0x5E;
    }
}
