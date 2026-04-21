namespace Trecs.Serialization
{
    public class SerializationConstants
    {
        /// <summary>
        /// Marker byte written at the end of every Trecs binary payload by
        /// <see cref="BinarySerializationWriter"/> and verified on read.
        /// Catches truncated streams and a narrow class of corruption. This
        /// is the payload-level marker — not to be confused with
        /// <c>WorldStateSerializer</c>'s internal <c>WorldStateStreamGuard</c>
        /// which guards the ECS-state section inside the payload.
        /// </summary>
        public const byte EndOfPayloadMarker = 0x5E;
    }
}
