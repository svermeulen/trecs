using System;
using System.IO;
using Trecs.Internal;

namespace Trecs.Serialization
{
    /// <summary>
    /// Captures and restores full ECS world-state snapshots as binary
    /// snapshots. Main-thread only.
    /// </summary>
    /// <remarks>
    /// The typical lifecycle is:
    /// <list type="number">
    /// <item><see cref="SaveSnapshot(int, Stream, bool)"/> or <see cref="SaveSnapshot(int, string, bool)"/>
    /// to capture the current world state.</item>
    /// <item><see cref="LoadSnapshot(Stream)"/> or <see cref="LoadSnapshot(string)"/> later
    /// to restore the saved state directly into the live world.</item>
    /// </list>
    /// Use <see cref="PeekMetadata(Stream)"/> to read just the header (frame
    /// number, schema version, blob refs) without rehydrating full state —
    /// handy for save-slot UIs.
    ///
    /// All methods are synchronous and run on the calling thread.
    /// </remarks>
    public class SnapshotSerializer : IDisposable
    {
        static readonly TrecsLog _log = new(nameof(SnapshotSerializer));

        readonly IWorldStateSerializer _worldStateSerializer;
        readonly SerializationBuffer _buffer;
        readonly BlobCache _blobCache;
        readonly WorldAccessor _world;

        bool _disposed;

        public SnapshotSerializer(
            IWorldStateSerializer worldStateSerializer,
            SerializerRegistry registry,
            World world
        )
        {
            if (registry == null)
                throw new ArgumentNullException(nameof(registry));
            if (world == null)
                throw new ArgumentNullException(nameof(world));

            _worldStateSerializer =
                worldStateSerializer
                ?? throw new ArgumentNullException(nameof(worldStateSerializer));
            _blobCache = world.GetBlobCache();
            _world = world.CreateAccessor();
            _buffer = new SerializationBuffer(registry);
        }

        /// <summary>
        /// Capture the current world state and write it to <paramref name="stream"/>.
        /// </summary>
        /// <param name="version">
        /// User-defined schema version. Saved in the metadata and exposed on load via
        /// <see cref="SnapshotMetadata.Version"/>; callers are responsible for deciding
        /// whether a snapshot is compatible with their current schema.
        /// </param>
        /// <param name="stream">Output stream. Must be writable.</param>
        /// <param name="includeTypeChecks">Include per-field type IDs in the binary output for stricter validation on load.</param>
        /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
        /// <exception cref="ObjectDisposedException">The snapshot serializer has been disposed.</exception>
        public SnapshotMetadata SaveSnapshot(
            int version,
            Stream stream,
            bool includeTypeChecks = true
        )
        {
            ThrowIfDisposed();
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            var metadata = new SnapshotMetadata
            {
                Version = version,
                FixedFrame = _world.FixedFrame,
            };
            _blobCache.GetAllActiveBlobIds(metadata.BlobIds);

            try
            {
                _buffer.ClearMemoryStream();
                _buffer.StartWrite(version: version, includeTypeChecks: includeTypeChecks);
                _buffer.Write("metadata", metadata);
                _worldStateSerializer.SerializeState(_buffer);
                var numBytes = _buffer.EndWrite();
                _log.Trace("Saved snapshot ({0.00} kb)", numBytes / 1024f);
            }
            catch
            {
                _buffer.ResetForErrorRecovery();
                throw;
            }

            _buffer.MemoryStream.Position = 0;
            _buffer.MemoryStream.CopyTo(stream);
            return metadata;
        }

        /// <summary>
        /// Capture the current world state and write it to <paramref name="filePath"/>.
        /// Creates the parent directory if it does not exist. Overwrites any
        /// existing file at the path.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="filePath"/> is null or empty.</exception>
        /// <exception cref="ObjectDisposedException">The snapshot serializer has been disposed.</exception>
        public SnapshotMetadata SaveSnapshot(
            int version,
            string filePath,
            bool includeTypeChecks = true
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
        /// Read a snapshot from <paramref name="stream"/> and restore it into the live world.
        /// </summary>
        /// <returns>The snapshot's header metadata (version, frame, blob refs).</returns>
        /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
        /// <exception cref="SerializationException">The stream is empty/truncated or the binary payload is invalid.</exception>
        /// <exception cref="ObjectDisposedException">The snapshot serializer has been disposed.</exception>
        public SnapshotMetadata LoadSnapshot(Stream stream)
        {
            ThrowIfDisposed();
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            try
            {
                LoadStreamIntoBuffer(stream);
                _buffer.StartRead();
                var metadata = _buffer.Read<SnapshotMetadata>("metadata");
                _worldStateSerializer.DeserializeState(_buffer);
                _buffer.StopRead(verifySentinel: true);
                return metadata;
            }
            catch
            {
                _buffer.ResetForErrorRecovery();
                throw;
            }
        }

        /// <summary>
        /// Read a snapshot from <paramref name="filePath"/> and restore it into the live world.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="filePath"/> is null or empty.</exception>
        /// <exception cref="FileNotFoundException">No file at <paramref name="filePath"/>.</exception>
        /// <exception cref="SerializationException">The file is empty/truncated or the binary payload is invalid.</exception>
        /// <exception cref="ObjectDisposedException">The snapshot serializer has been disposed.</exception>
        public SnapshotMetadata LoadSnapshot(string filePath)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("filePath must be non-empty", nameof(filePath));
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Snapshot file not found", filePath);

            using var fs = File.OpenRead(filePath);
            return LoadSnapshot(fs);
        }

        /// <summary>
        /// Read just the snapshot metadata from <paramref name="stream"/> without
        /// restoring the full world state. Useful for displaying save-slot info
        /// (e.g. timestamp, frame number, schema version) in UI without a full load.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
        /// <exception cref="SerializationException">The stream is empty/truncated or the binary payload is invalid.</exception>
        /// <exception cref="ObjectDisposedException">The snapshot serializer has been disposed.</exception>
        public SnapshotMetadata PeekMetadata(Stream stream)
        {
            ThrowIfDisposed();
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            try
            {
                LoadStreamIntoBuffer(stream);
                _buffer.StartRead();
                var metadata = _buffer.Read<SnapshotMetadata>("metadata");
                _buffer.StopRead(verifySentinel: false);
                return metadata;
            }
            catch
            {
                _buffer.ResetForErrorRecovery();
                throw;
            }
        }

        /// <summary>
        /// Read just the snapshot metadata from <paramref name="filePath"/> without
        /// restoring the full world state.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="filePath"/> is null or empty.</exception>
        /// <exception cref="FileNotFoundException">No file at <paramref name="filePath"/>.</exception>
        /// <exception cref="SerializationException">The file is empty/truncated or the binary payload is invalid.</exception>
        /// <exception cref="ObjectDisposedException">The snapshot serializer has been disposed.</exception>
        public SnapshotMetadata PeekMetadata(string filePath)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("filePath must be non-empty", nameof(filePath));
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Snapshot file not found", filePath);

            using var fs = File.OpenRead(filePath);
            return PeekMetadata(fs);
        }

        void LoadStreamIntoBuffer(Stream stream)
        {
            _buffer.ClearMemoryStream();
            stream.CopyTo(_buffer.MemoryStream);
            if (_buffer.MemoryStream.Length == 0)
            {
                throw new SerializationException(
                    "Snapshot stream is empty — cannot load an empty snapshot."
                );
            }
            _buffer.MemoryStream.Position = 0;
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
            _buffer.Dispose();
        }
    }
}
