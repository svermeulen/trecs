using System;
using System.IO;
using System.Text;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Owns the opaque-blob byte format in both directions: <see cref="SerializeResidentBlob"/>
    /// bakes a resident <see cref="BlobCache"/> blob out to bytes, and <see cref="Deserialize"/>
    /// reads them back into the resident representation — a managed (class) blob through this baker's
    /// own serialization scratch, a native (unmanaged) blob through <see cref="NativeBlobFileFormat"/>.
    /// Snapshot/recording persistence uses both halves directly; the out-of-core loader in
    /// com.trecs.addressables (<c>OpaqueBlobSource</c>) reuses <see cref="Deserialize"/> for the same
    /// format.
    ///
    /// <para>Owns a reusable managed-path scratch (a writer/reader pair plus its write and read
    /// buffers); reused across calls, so a single instance held by a caller pays no per-blob
    /// allocation. Stateful — main-thread only.</para>
    /// </summary>
    public sealed class OpaqueBlobBaker
    {
        /// <summary>
        /// The opaque-blob byte format version, written into each baked payload and validated on read
        /// (see <see cref="NativeBlobFileFormat"/> for the native path; the managed path threads it
        /// through serialization). Bump this when the opaque-blob byte layout changes so stale bytes
        /// are rejected with a clear mismatch rather than silently misread. It is deliberately
        /// independent of the snapshot / input-stream serialization versions — an opaque blob is a
        /// self-contained side payload, content-addressed and shared across the snapshot and input
        /// (recording) paths, so all of those callers bake/read it at this one version. The single
        /// source of truth: callers pass this rather than re-declaring their own constant.
        /// </summary>
        public const int CurrentFormatVersion = 1;

        readonly SerializationHelper _helper;
        readonly SerializationData _writeScratch = new(); // managed-path write buffer; emitted via WriteContiguousTo
        readonly SerializationReadBuffer _readScratch = new(); // managed-path read buffer; drains the source stream

        public OpaqueBlobBaker(SerializerRegistry registry)
        {
            if (registry == null)
                throw new ArgumentNullException(nameof(registry));
            _helper = new SerializationHelper(registry);
        }

        /// <summary>
        /// Serializes the blob currently registered at <paramref name="id"/> in
        /// <paramref name="cache"/> (materializing it if not resident) straight into
        /// <paramref name="destination"/>, in the format <see cref="Deserialize"/> reads back. A
        /// native blob streams directly out of its <c>NativeBlobBox</c> with no managed
        /// <c>byte[]</c>; a managed blob is serialized into this baker's scratch and its contiguous
        /// form written out. <paramref name="serializationVersion"/> must match the value it is later
        /// deserialized with (defaults to <see cref="CurrentFormatVersion"/>). Main-thread only.
        /// </summary>
        public void SerializeResidentBlob(
            BlobCache cache,
            BlobId id,
            Stream destination,
            int serializationVersion = CurrentFormatVersion
        )
        {
            SerializeResidentBlobCore(cache, id, destination, serializationVersion);
        }

        /// <summary>
        /// <see cref="SerializeResidentBlob(BlobCache, BlobId, Stream, int)"/> for callers that hold a
        /// <see cref="WorldAccessor"/> rather than the framework-internal <see cref="BlobCache"/> —
        /// e.g. an editor bake pipeline persisting opaque blobs to disk. Writes the same bytes and
        /// additionally returns the blob's <see cref="BlobMetadata"/> (its <see cref="TypeId"/> and
        /// native flag), which a bake manifest entry needs alongside the bytes. Main-thread only.
        /// </summary>
        public BlobMetadata SerializeResidentBlob(
            WorldAccessor world,
            BlobId id,
            Stream destination,
            int serializationVersion = CurrentFormatVersion
        )
        {
            TrecsAssert.That(world != null, "world must not be null");
            return SerializeResidentBlobCore(
                world.BlobCache,
                id,
                destination,
                serializationVersion
            );
        }

        // Shared body for both overloads: serializes the blob and returns the metadata it reads in
        // passing (the WorldAccessor overload hands it back; the BlobCache overload discards it).
        BlobMetadata SerializeResidentBlobCore(
            BlobCache cache,
            BlobId id,
            Stream destination,
            int serializationVersion
        )
        {
            TrecsAssert.That(cache != null, "cache must not be null");
            TrecsAssert.That(destination != null, "destination must not be null");

            var blob = cache.GetBlobAndMetadata(id, out var metadata);

            if (metadata.IsNative)
            {
                var box = (NativeBlobBox)blob;
                // leaveOpen: the destination stream is the caller's (e.g. the store's temp file); the
                // BinaryWriter must not close it. NativeBlobFileFormat writes box bytes straight
                // through to it.
                using var writer = new BinaryWriter(destination, Encoding.UTF8, leaveOpen: true);
                NativeBlobFileFormat.Write(writer, serializationVersion, box);
                writer.Flush();
                return metadata;
            }

            _helper.WriteAllObject(
                _writeScratch,
                blob,
                version: serializationVersion,
                includeTypeChecks: true
            );
            _writeScratch.WriteContiguousTo(destination);
            return metadata;
        }

        /// <summary>
        /// Inverse of <see cref="SerializeResidentBlob"/>: deserialize <paramref name="source"/> back
        /// into the resident representation — a <see cref="NativeBlobBox"/> (rented from
        /// <paramref name="pool"/>) for a native blob, or a managed object for a managed one. The
        /// caller seeds the cache with the result (e.g. <c>BlobCache.InsertEagerBlob</c>); this
        /// owns only the byte format, not the cache lifecycle. Reads from the current position to the
        /// end of <paramref name="source"/> and leaves it open. <paramref name="serializationVersion"/>
        /// must match the value the bytes were baked with. Main-thread only.
        /// </summary>
        public object Deserialize(
            Stream source,
            Type blobType,
            bool isNative,
            int serializationVersion,
            NativeBlobBoxPool pool
        )
        {
            TrecsAssert.That(source != null, "source must not be null");

            if (isNative)
            {
                TrecsAssert.That(pool != null, "pool must not be null for a native opaque blob");
                using var reader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);
                return NativeBlobFileFormat.Read(
                    reader,
                    source,
                    serializationVersion,
                    blobType,
                    $"opaque blob of type {blobType}",
                    pool
                );
            }

            var value = _helper.ReadAllObject(_readScratch.Load(source));

            TrecsAssert.That(
                value != null && blobType.IsInstanceOfType(value),
                "Deserialized opaque blob was not assignable to expected type {0}",
                blobType
            );
            return value;
        }
    }
}
