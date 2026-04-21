using System;
using System.IO;
using Trecs.Internal;

namespace Trecs.Serialization
{
    /// <summary>
    /// Captures and restores full ECS world-state snapshots as binary
    /// bookmarks. Main-thread only.
    /// </summary>
    /// <remarks>
    /// The typical lifecycle is:
    /// <list type="number">
    /// <item><see cref="SaveBookmark(int, Stream, bool, int)"/> or <see cref="SaveBookmark(int, string, bool, int)"/>
    /// to capture the current world state.</item>
    /// <item><see cref="LoadBookmark(Stream)"/> or <see cref="LoadBookmark(string)"/> later
    /// to restore the saved state directly into the live world.</item>
    /// </list>
    /// Use <see cref="PeekMetadata(Stream)"/> to read just the header (frame
    /// number, schema version, blob refs) without rehydrating full state —
    /// handy for save-slot UIs.
    ///
    /// All methods are synchronous and run on the calling thread.
    /// </remarks>
    public class BookmarkSerializer : IDisposable
    {
        static readonly TrecsLog _log = new(nameof(BookmarkSerializer));

        readonly WorldStateSerializer _worldStateSerializer;
        readonly SerializationBuffer _buffer;
        readonly BlobCache _blobCache;
        readonly WorldAccessor _world;

        bool _disposed;

        public BookmarkSerializer(
            WorldStateSerializer worldStateSerializer,
            SerializerRegistry registry,
            World world
        )
        {
            if (worldStateSerializer == null)
                throw new ArgumentNullException(nameof(worldStateSerializer));
            if (registry == null)
                throw new ArgumentNullException(nameof(registry));
            if (world == null)
                throw new ArgumentNullException(nameof(world));

            _worldStateSerializer = worldStateSerializer;
            _blobCache = world.GetBlobCache();
            _world = world.CreateAccessor();
            _buffer = new SerializationBuffer(registry);
        }

        /// <summary>
        /// Capture the current world state and write it to <paramref name="stream"/>.
        /// </summary>
        /// <param name="version">
        /// User-defined schema version. Saved in the metadata and exposed on load via
        /// <see cref="BookmarkMetadata.Version"/>; callers are responsible for deciding
        /// whether a bookmark is compatible with their current schema.
        /// </param>
        /// <param name="stream">Output stream. Must be writable.</param>
        /// <param name="includeTypeChecks">Include per-field type IDs in the binary output for stricter validation on load.</param>
        /// <param name="numConnections">Optional metadata field for host/multiplayer bookmarks.</param>
        /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
        /// <exception cref="ObjectDisposedException">The bookmark serializer has been disposed.</exception>
        public BookmarkMetadata SaveBookmark(
            int version,
            Stream stream,
            bool includeTypeChecks = true,
            int numConnections = 0
        )
        {
            ThrowIfDisposed();
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            var metadata = new BookmarkMetadata
            {
                Version = version,
                NumConnections = numConnections,
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
                _log.Trace("Saved bookmark ({0.00} kb)", numBytes / 1024f);
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
        /// <exception cref="ObjectDisposedException">The bookmark serializer has been disposed.</exception>
        public BookmarkMetadata SaveBookmark(
            int version,
            string filePath,
            bool includeTypeChecks = true,
            int numConnections = 0
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
            return SaveBookmark(version, fs, includeTypeChecks, numConnections);
        }

        /// <summary>
        /// Read a bookmark from <paramref name="stream"/> and restore it into the live world.
        /// </summary>
        /// <returns>The bookmark's header metadata (version, frame, blob refs).</returns>
        /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
        /// <exception cref="SerializationException">The stream is empty/truncated or the binary payload is invalid.</exception>
        /// <exception cref="ObjectDisposedException">The bookmark serializer has been disposed.</exception>
        public BookmarkMetadata LoadBookmark(Stream stream)
        {
            ThrowIfDisposed();
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            try
            {
                LoadStreamIntoBuffer(stream);
                _buffer.StartRead();
                var metadata = _buffer.Read<BookmarkMetadata>("metadata");
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
        /// Read a bookmark from <paramref name="filePath"/> and restore it into the live world.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="filePath"/> is null or empty.</exception>
        /// <exception cref="FileNotFoundException">No file at <paramref name="filePath"/>.</exception>
        /// <exception cref="SerializationException">The file is empty/truncated or the binary payload is invalid.</exception>
        /// <exception cref="ObjectDisposedException">The bookmark serializer has been disposed.</exception>
        public BookmarkMetadata LoadBookmark(string filePath)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("filePath must be non-empty", nameof(filePath));
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Bookmark file not found", filePath);

            using var fs = File.OpenRead(filePath);
            return LoadBookmark(fs);
        }

        /// <summary>
        /// Read just the bookmark metadata from <paramref name="stream"/> without
        /// restoring the full world state. Useful for displaying save-slot info
        /// (e.g. timestamp, frame number, schema version) in UI without a full load.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
        /// <exception cref="SerializationException">The stream is empty/truncated or the binary payload is invalid.</exception>
        /// <exception cref="ObjectDisposedException">The bookmark serializer has been disposed.</exception>
        public BookmarkMetadata PeekMetadata(Stream stream)
        {
            ThrowIfDisposed();
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            try
            {
                LoadStreamIntoBuffer(stream);
                _buffer.StartRead();
                var metadata = _buffer.Read<BookmarkMetadata>("metadata");
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
        /// Read just the bookmark metadata from <paramref name="filePath"/> without
        /// restoring the full world state.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="filePath"/> is null or empty.</exception>
        /// <exception cref="FileNotFoundException">No file at <paramref name="filePath"/>.</exception>
        /// <exception cref="SerializationException">The file is empty/truncated or the binary payload is invalid.</exception>
        /// <exception cref="ObjectDisposedException">The bookmark serializer has been disposed.</exception>
        public BookmarkMetadata PeekMetadata(string filePath)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("filePath must be non-empty", nameof(filePath));
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Bookmark file not found", filePath);

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
                    "Bookmark stream is empty — cannot load an empty bookmark."
                );
            }
            _buffer.MemoryStream.Position = 0;
        }

        void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(BookmarkSerializer));
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
