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

        /// <summary>
        /// Upper bound on collection element counts read from serialized data.
        /// On load, a stream claiming more than this many items is rejected
        /// rather than driving an allocation that would either OOM or spend a
        /// long time reading garbage. Applies uniformly to lists, arrays, and
        /// dictionaries deserialized via the built-in serializers.
        ///
        /// Raise this if you legitimately need to serialize larger collections.
        /// </summary>
        public static int MaxCollectionLength = 1_000_000;
    }
}
