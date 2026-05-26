using System;
using System.IO;

namespace Trecs.Internal
{
    /// <summary>
    /// Captures and restores full ECS world-state snapshots as binary
    /// snapshots. Main-thread only.
    /// </summary>
    public sealed class SnapshotSerializer : IDisposable
    {
        readonly TrecsLog _log;

        readonly IWorldStateSerializer _worldStateSerializer;
        readonly BinarySerializationWriter _writer;
        readonly BinarySerializationReader _reader;
        readonly BlobCache _blobCache;
        readonly WorldAccessor _world;
        readonly SnapshotPayloadPool _pool;

        // Used only by the in-memory save paths (out-payload and checksum).
        // The stream-only save path writes directly to the caller's stream.
        readonly MemoryStream _memoryStream;

        readonly SnapshotMetadata _metadata = new();

        bool _disposed;

        public SnapshotSerializer(
            IWorldStateSerializer worldStateSerializer,
            SerializerRegistry registry,
            World world,
            SnapshotPayloadPool pool = null
        )
        {
            if (registry == null)
                throw new ArgumentNullException(nameof(registry));
            if (world == null)
                throw new ArgumentNullException(nameof(world));

            _log = world.Log;
            _worldStateSerializer =
                worldStateSerializer
                ?? throw new ArgumentNullException(nameof(worldStateSerializer));
            _blobCache = world.BlobCache;
            _world = world.CreateAccessor(AccessorRole.Unrestricted);
            _writer = new BinarySerializationWriter(registry);
            _reader = new BinarySerializationReader(registry);
            _memoryStream = new MemoryStream(1024);
            _pool = pool;
        }

        /// <summary>
        /// Capture the current world state and write it to <paramref name="stream"/>.
        /// </summary>
        public SnapshotMetadata SaveSnapshot(
            int version,
            Stream stream,
            bool includeTypeChecks = false
        )
        {
            ThrowIfDisposed();
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            using var _profile = TrecsProfiling.Start("SnapshotSerializer.SaveSnapshot");

            SerializeToStream(version, includeTypeChecks, stream);
            return _metadata;
        }

        /// <summary>
        /// Capture the current world state into a pooled payload buffer and
        /// return an exact-length view over it, plus a 64-bit xxHash checksum.
        /// Requires a <see cref="SnapshotPayloadPool"/> to have been provided
        /// at construction time.
        /// </summary>
        public SnapshotMetadata SaveSnapshot(
            int version,
            out ReadOnlyMemory<byte> payload,
            out ulong checksum,
            bool includeTypeChecks = false
        )
        {
            ThrowIfDisposed();
            TrecsAssert.That(
                _pool != null,
                "SaveSnapshot with out-payload requires a SnapshotPayloadPool"
            );

            using var _profile = TrecsProfiling.Start("SnapshotSerializer.SaveSnapshot");

            payload = SerializeToPooledBuffer(version, includeTypeChecks);

            ulong checksumValue;
            using (TrecsProfiling.Start("ComputeChecksum"))
            {
                checksumValue = CollisionResistantHashCalculator.ComputeXxHash64(payload);
            }
            checksum = checksumValue;

            return _metadata;
        }

        /// <summary>
        /// Capture the current world state into a pooled payload buffer.
        /// Same as the checksum overload but skips the xxHash computation.
        /// Requires a <see cref="SnapshotPayloadPool"/> to have been provided
        /// at construction time.
        /// </summary>
        public SnapshotMetadata SaveSnapshot(
            int version,
            out ReadOnlyMemory<byte> payload,
            bool includeTypeChecks = false
        )
        {
            ThrowIfDisposed();
            TrecsAssert.That(
                _pool != null,
                "SaveSnapshot with out-payload requires a SnapshotPayloadPool"
            );

            using var _profile = TrecsProfiling.Start("SnapshotSerializer.SaveSnapshot");

            payload = SerializeToPooledBuffer(version, includeTypeChecks);
            return _metadata;
        }

        /// <summary>
        /// Capture the current world state and write it to <paramref name="filePath"/>.
        /// </summary>
        public SnapshotMetadata SaveSnapshot(
            int version,
            string filePath,
            bool includeTypeChecks = false
        )
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("filePath must be non-empty", nameof(filePath));

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var fs = File.Create(filePath);
            return SaveSnapshot(version, fs, includeTypeChecks);
        }

        /// <summary>
        /// Compute the 64-bit xxHash checksum of the current world state
        /// without producing a retained payload buffer.
        /// </summary>
        public ulong ComputeChecksum(int version, bool includeTypeChecks = false)
        {
            ThrowIfDisposed();

            using var _profile = TrecsProfiling.Start("SnapshotSerializer.ComputeChecksum");

            _memoryStream.Position = 0;
            _memoryStream.SetLength(0);
            SerializeToStream(version, includeTypeChecks, _memoryStream);

            return CollisionResistantHashCalculator.ComputeXxHash64(
                _memoryStream.GetBuffer(),
                (int)_memoryStream.Length
            );
        }

        /// <summary>
        /// Read a snapshot from <paramref name="stream"/> and restore it into the live world.
        /// </summary>
        public SnapshotMetadata LoadSnapshot(Stream stream)
        {
            ThrowIfDisposed();
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            using var _profile = TrecsProfiling.Start("SnapshotSerializer.LoadSnapshot");

            using var temp = new MemoryStream();
            stream.CopyTo(temp);
            if (temp.Length == 0)
            {
                throw new SerializationException(
                    "Snapshot stream is empty — cannot load an empty snapshot."
                );
            }

            return LoadFromMemory(new ReadOnlyMemory<byte>(temp.GetBuffer(), 0, (int)temp.Length));
        }

        /// <summary>
        /// Read a snapshot from in-memory bytes and restore it into the live world.
        /// </summary>
        public SnapshotMetadata LoadSnapshot(ReadOnlyMemory<byte> payload)
        {
            ThrowIfDisposed();
            if (payload.IsEmpty)
            {
                throw new SerializationException(
                    "Snapshot payload is empty — cannot load an empty snapshot."
                );
            }

            using var _profile = TrecsProfiling.Start("SnapshotSerializer.LoadSnapshot");

            return LoadFromMemory(payload);
        }

        /// <summary>
        /// Read a snapshot from <paramref name="filePath"/> and restore it into the live world.
        /// </summary>
        public SnapshotMetadata LoadSnapshot(string filePath)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("filePath must be non-empty", nameof(filePath));
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Snapshot file not found", filePath);

            var bytes = File.ReadAllBytes(filePath);
            return LoadSnapshot(new ReadOnlyMemory<byte>(bytes));
        }

        /// <summary>
        /// Read just the snapshot metadata from <paramref name="stream"/> without
        /// restoring the full world state.
        /// </summary>
        public SnapshotMetadata PeekMetadata(Stream stream)
        {
            ThrowIfDisposed();
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            using var temp = new MemoryStream();
            stream.CopyTo(temp);
            if (temp.Length == 0)
            {
                throw new SerializationException(
                    "Snapshot stream is empty — cannot peek an empty snapshot."
                );
            }

            return PeekMetadataFromMemory(
                new ReadOnlyMemory<byte>(temp.GetBuffer(), 0, (int)temp.Length)
            );
        }

        /// <summary>
        /// Read just the snapshot metadata from <paramref name="filePath"/> without
        /// restoring the full world state.
        /// </summary>
        public SnapshotMetadata PeekMetadata(string filePath)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("filePath must be non-empty", nameof(filePath));
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Snapshot file not found", filePath);

            var bytes = File.ReadAllBytes(filePath);
            return PeekMetadataFromMemory(new ReadOnlyMemory<byte>(bytes));
        }

        // -- internals -------------------------------------------------------

        void PrepareMetadata(int version)
        {
            _metadata.Version = version;
            _metadata.FixedFrame = _world.FixedFrame;
            _metadata.BlobIds.Clear();
            _blobCache.GetAllActiveBlobIds(_metadata.BlobIds);
        }

        void SerializeToStream(int version, bool includeTypeChecks, Stream target)
        {
            PrepareMetadata(version);

            try
            {
                using (TrecsProfiling.Start("SerializationWriter.Start"))
                {
                    _writer.Start(version: version, includeTypeChecks: includeTypeChecks);
                    _writer.Write("Metadata", _metadata);
                }
                _worldStateSerializer.SerializeFullState(_writer);
                using (TrecsProfiling.Start("SerializationWriter.Flush"))
                {
                    _writer.Complete(target);
                }
            }
            catch
            {
                _writer.ResetForErrorRecovery();
                throw;
            }
        }

        ReadOnlyMemory<byte> SerializeToPooledBuffer(int version, bool includeTypeChecks)
        {
            PrepareMetadata(version);

            try
            {
                using (TrecsProfiling.Start("SerializationWriter.Start"))
                {
                    _writer.Start(version: version, includeTypeChecks: includeTypeChecks);
                    _writer.Write("Metadata", _metadata);
                }
                _worldStateSerializer.SerializeFullState(_writer);

                int totalSize = _writer.ComputeOutputSize();
                var buffer = _pool.Rent(totalSize);

                using (TrecsProfiling.Start("SerializationWriter.Flush"))
                {
                    _writer.CompleteTo(buffer);
                }

                _log.Trace("Saved snapshot ({0:0.00} kb)", totalSize / 1024f);
                return new ReadOnlyMemory<byte>(buffer, 0, totalSize);
            }
            catch
            {
                _writer.ResetForErrorRecovery();
                throw;
            }
        }

        SnapshotMetadata LoadFromMemory(ReadOnlyMemory<byte> data)
        {
            try
            {
                _reader.Start(data);
                var metadata = _reader.Read<SnapshotMetadata>("Metadata");
                _worldStateSerializer.DeserializeState(_reader);
                _reader.Stop(verifySentinel: true);
                return metadata;
            }
            catch
            {
                _reader.ResetForErrorRecovery();
                throw;
            }
        }

        SnapshotMetadata PeekMetadataFromMemory(ReadOnlyMemory<byte> data)
        {
            try
            {
                _reader.Start(data);
                var metadata = _reader.Read<SnapshotMetadata>("Metadata");
                _reader.Stop(verifySentinel: false);
                return metadata;
            }
            catch
            {
                _reader.ResetForErrorRecovery();
                throw;
            }
        }

        void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SnapshotSerializer));
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _memoryStream.Dispose();
        }
    }
}
