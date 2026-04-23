namespace Trecs.Serialization
{
    /// <summary>
    /// Well-known bitflags for serialization context. Flags are written into the
    /// payload header and recovered by the reader automatically — user serializers
    /// branch on <c>writer.HasFlag(...)</c> or <c>reader.HasFlag(...)</c> to
    /// include/exclude context-specific data.
    ///
    /// <para>
    /// <b>Bit ownership:</b> bits 0..<see cref="FirstUserBitIndex"/>-1 are reserved
    /// for Trecs-internal flags. Apps built on Trecs define their own flags at
    /// <see cref="FirstUserBitIndex"/> or higher — for example:
    /// </para>
    ///
    /// <code>
    /// public static class MyAppFlags
    /// {
    ///     public const long IsForNetwork = 1L &lt;&lt; (SerializationFlags.FirstUserBitIndex + 0);
    ///     public const long IsForRollback = 1L &lt;&lt; (SerializationFlags.FirstUserBitIndex + 1);
    /// }
    /// </code>
    ///
    /// <para>
    /// Using a bit below <see cref="FirstUserBitIndex"/> without going through a
    /// <c>SerializationFlags.*</c> constant will trip the reserved-bit assertion
    /// on the write path.
    /// </para>
    /// </summary>
    public static class SerializationFlags
    {
        /// <summary>
        /// Set when the serialized bytes will be hashed for desync detection.
        /// Serializers should skip any non-deterministic state (timing-dependent
        /// queues, variable-update event buffers, etc.) under this flag.
        /// </summary>
        public const long IsForChecksum = 1L << 0;

        /// <summary>
        /// First bit available for app-defined flags. Bits below this index are
        /// reserved for future Trecs-internal flags and must not be used directly
        /// by application code.
        /// </summary>
        public const int FirstUserBitIndex = 4;

        /// <summary>
        /// Mask of bits reserved for Trecs-internal flags. Used by the write path
        /// to detect user code grabbing reserved bits.
        /// </summary>
        public const long ReservedMask = (1L << FirstUserBitIndex) - 1;

        /// <summary>
        /// Union of all Trecs-defined flags currently in use. Any bit set in
        /// <see cref="ReservedMask"/> but not in this constant is reserved-but-
        /// unassigned, and user code passing it is almost certainly a mistake.
        /// </summary>
        public const long AllDefinedMask = IsForChecksum;
    }
}
