namespace Trecs.Internal
{
    /// <summary>
    /// The caller-owned working space <see cref="SnapshotSerializer"/> needs — the core is
    /// stateless, so every consumer holds one of these and reuses it across calls instead of
    /// allocating per snapshot. Groups the three pieces that always travel together:
    /// <list type="bullet">
    /// <item><see cref="Metadata"/> — the metadata working instance populated on save/checksum
    /// (and on loads whose caller doesn't retain the metadata).</item>
    /// <item><see cref="ChecksumData"/> — the throwaway byte target for checksum passes, which
    /// serialize a full snapshot but discard the payload.</item>
    /// <item><see cref="ReadBuffer"/> — the read view that turns a contiguous snapshot payload
    /// (a drained stream, a .snap blob, a bundle's embedded snapshot) into an
    /// <see cref="IReadOnlySerializationData"/> without copying.</item>
    /// </list>
    /// Main-thread only, like the core itself; one instance per owner.
    /// </summary>
    public sealed class SnapshotSerializerScratch
    {
        public readonly SnapshotMetadata Metadata = new();
        public readonly SerializationData ChecksumData = new();
        public readonly SerializationReadBuffer ReadBuffer = new();
    }
}
