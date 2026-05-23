using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Trecs.Internal
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
    public sealed class SnapshotSerializer : IDisposable
    {
        /// <summary>
        /// Default upper bound on the number of recycled payload buffers the
        /// serializer keeps in its pool. Snapshots are typically multi-MB and
        /// live on the LOH, so this bound also bounds resident LOH memory; pick
        /// a value that comfortably covers any caller's working set of in-
        /// memory snapshots (e.g. AutoRecorder's anchor cap + scrub-cache
        /// peak). Beyond the cap, returned buffers are simply abandoned to
        /// the GC.
        /// </summary>
        public const int DefaultMaxPoolBuffers = 64;

        readonly TrecsLog _log;

        readonly IWorldStateSerializer _worldStateSerializer;
        readonly SerializationBuffer _buffer;
        readonly BlobCache _blobCache;
        readonly WorldAccessor _world;

        // Reused across SaveSnapshot calls. Returned to callers, but they only
        // read FixedFrame and don't retain references — the next SaveSnapshot
        // safely mutates it in place. Load paths construct fresh instances
        // through the deserializer so they don't share this state.
        readonly SnapshotMetadata _metadata = new();

        // Pool of recycled payload byte[]s. Multi-MB allocations live on the
        // LOH and would trigger Gen 2 GC on every capture; recycling avoids
        // that. Capped by _maxPoolBuffers to bound resident memory. Buffers
        // grow in place when SaveSnapshot needs more capacity — never shrink.
        readonly Stack<byte[]> _payloadPool = new();
        readonly int _maxPoolBuffers;
#if DEBUG
        // Mirror set of the pool used only for O(1) double-return detection.
        // byte[] inherits Object.Equals/GetHashCode (reference equality) so a
        // plain HashSet keys on reference identity.
        readonly HashSet<byte[]> _payloadPoolSet = new();
#endif

        bool _disposed;

        public SnapshotSerializer(
            IWorldStateSerializer worldStateSerializer,
            SerializerRegistry registry,
            World world,
            int maxPoolBuffers = DefaultMaxPoolBuffers
        )
        {
            if (registry == null)
                throw new ArgumentNullException(nameof(registry));
            if (world == null)
                throw new ArgumentNullException(nameof(world));
            if (maxPoolBuffers < 0)
                throw new ArgumentOutOfRangeException(
                    nameof(maxPoolBuffers),
                    maxPoolBuffers,
                    "maxPoolBuffers must be >= 0"
                );

            _log = world.Log;
            _worldStateSerializer =
                worldStateSerializer
                ?? throw new ArgumentNullException(nameof(worldStateSerializer));
            _blobCache = world.BlobCache;
            _world = world.CreateAccessor(AccessorRole.Unrestricted);
            _buffer = new SerializationBuffer(registry);
            _maxPoolBuffers = maxPoolBuffers;
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

            using var _profile = TrecsProfiling.Start("SnapshotSerializer.SaveSnapshot");

            PrepareMetadata(version);

            try
            {
                _buffer.ClearMemoryStream();
                _buffer.StartWrite(version: version, includeTypeChecks: includeTypeChecks);
                _buffer.Write("Metadata", _metadata);
                _worldStateSerializer.SerializeFullState(_buffer.Writer);
                var numBytes = _buffer.EndWrite();
                _log.Trace("Saved snapshot ({0:0.00} kb)", numBytes / 1024f);
            }
            catch
            {
                _buffer.ResetForErrorRecovery();
                throw;
            }

            _buffer.MemoryStream.Position = 0;
            _buffer.MemoryStream.CopyTo(stream);
            return _metadata;
        }

        void PrepareMetadata(int version)
        {
            _metadata.Version = version;
            _metadata.FixedFrame = _world.FixedFrame;
            _metadata.BlobIds.Clear();
            _blobCache.GetAllActiveBlobIds(_metadata.BlobIds);
        }

        /// <summary>
        /// Capture the current world state into a pooled payload buffer and
        /// return an exact-length view over it. The returned
        /// <see cref="ReadOnlyMemory{T}"/> is sized to the actual payload —
        /// callers should not look at the underlying buffer's full length,
        /// which may be larger when a recycled buffer is reused.
        ///
        /// <para>
        /// When the caller is done with the payload, pass it back via
        /// <see cref="ReturnPayloadBuffer"/> so the backing byte[] is
        /// recycled instead of GC'd. Forgetting to call
        /// <see cref="ReturnPayloadBuffer"/> is safe (the buffer just doesn't
        /// recycle); calling it twice is a bug (the same buffer enters the
        /// pool twice and a future <see cref="SaveSnapshot(int, out ReadOnlyMemory{byte}, bool)"/>
        /// hands it out to two callers).
        /// </para>
        ///
        /// <para>
        /// The payload is only safe to read while the underlying buffer is
        /// not yet recycled. In practice that means: don't return the buffer
        /// while another consumer still holds a reference to the
        /// <see cref="ReadOnlyMemory{T}"/>, and don't capture another
        /// snapshot through the same serializer before reading the bytes if
        /// the pool happened to be empty (a fresh capture into a pooled
        /// buffer can mutate the bytes the previous slice references).
        /// </para>
        /// </summary>
        public SnapshotMetadata SaveSnapshot(
            int version,
            out ReadOnlyMemory<byte> payload,
            bool includeTypeChecks = true
        )
        {
            ThrowIfDisposed();

            using var _profile = TrecsProfiling.Start("SnapshotSerializer.SaveSnapshot");

            PrepareMetadata(version);

            try
            {
                _buffer.ClearMemoryStream();
                _buffer.StartWrite(version: version, includeTypeChecks: includeTypeChecks);
                _buffer.Write("Metadata", _metadata);
                _worldStateSerializer.SerializeFullState(_buffer.Writer);
                _buffer.EndWrite();
            }
            catch
            {
                _buffer.ResetForErrorRecovery();
                throw;
            }

            var length = (int)_buffer.MemoryStream.Length;
            // Pop a recycled buffer when one fits; allocate fresh otherwise.
            // Buffers grow in place — they never shrink — so any pooled buffer
            // that's >= length works.
            byte[] buffer = null;
            while (_payloadPool.Count > 0)
            {
                var candidate = _payloadPool.Pop();
#if DEBUG
                if (candidate != null)
                {
                    _payloadPoolSet.Remove(candidate);
                }
#endif
                if (candidate != null && candidate.Length >= length)
                {
                    buffer = candidate;
                    break;
                }
                // Too small — drop on the floor; a fresh allocation below is
                // cheaper than carrying tiny buffers forward through evictions.
            }
            if (buffer == null)
            {
                buffer = new byte[length];
            }
            Buffer.BlockCopy(_buffer.MemoryStream.GetBuffer(), 0, buffer, 0, length);
            payload = new ReadOnlyMemory<byte>(buffer, 0, length);
            _log.Trace("Saved snapshot ({0:0.00} kb)", length / 1024f);
            return _metadata;
        }

        /// <summary>
        /// Return the byte[] backing a <paramref name="payload"/> previously
        /// rented via <see cref="SaveSnapshot(int, out ReadOnlyMemory{byte}, bool)"/>
        /// to the serializer's pool. No-op for empty payloads and for payloads
        /// not backed by an array. The pool is bounded — past the cap the
        /// buffer is dropped on the floor for the GC to reclaim.
        ///
        /// <para>
        /// Calling this transfers ownership of the underlying byte[] from the
        /// caller back to the pool. After calling, the caller must not read
        /// from <paramref name="payload"/> — the next <see cref="SaveSnapshot(int, out ReadOnlyMemory{byte}, bool)"/>
        /// may pop this buffer and overwrite the bytes.
        /// </para>
        ///
        /// <para>
        /// Calling this twice with the same payload is a bug: the underlying
        /// byte[] would enter the pool twice, and two subsequent
        /// <see cref="SaveSnapshot(int, out ReadOnlyMemory{byte}, bool)"/>
        /// calls would hand the same buffer to two different consumers —
        /// they'd then corrupt each other's payloads. DEBUG builds assert on
        /// double-return via a side HashSet (O(1) per call); release builds
        /// rely on the caller to track ownership.
        /// </para>
        /// </summary>
        public void ReturnPayloadBuffer(ReadOnlyMemory<byte> payload)
        {
            if (payload.IsEmpty)
            {
                return;
            }
            if (_payloadPool.Count >= _maxPoolBuffers)
            {
                return;
            }
            if (
                MemoryMarshal.TryGetArray(payload, out var seg)
                && seg.Array != null
                && seg.Array.Length > 0
            )
            {
#if DEBUG
                TrecsDebugAssert.That(
                    _payloadPoolSet.Add(seg.Array),
                    "SnapshotSerializer payload buffer returned twice — caller tracking is broken"
                );
#endif
                _payloadPool.Push(seg.Array);
            }
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

            using var _profile = TrecsProfiling.Start("SnapshotSerializer.LoadSnapshot");

            try
            {
                LoadStreamIntoBuffer(stream);
                return LoadFromInternalBuffer();
            }
            catch
            {
                _buffer.ResetForErrorRecovery();
                throw;
            }
        }

        /// <summary>
        /// Read a snapshot directly from an in-memory byte span and restore it
        /// into the live world. Avoids wrapping the bytes in a
        /// <see cref="MemoryStream"/> — preferred when the payload is already
        /// resident in memory (e.g. a <see cref="RecordingBundle"/>'s anchor).
        /// </summary>
        /// <returns>The snapshot's header metadata.</returns>
        /// <exception cref="SerializationException">The span is empty/truncated or the binary payload is invalid.</exception>
        /// <exception cref="ObjectDisposedException">The snapshot serializer has been disposed.</exception>
        public SnapshotMetadata LoadSnapshot(ReadOnlySpan<byte> payload)
        {
            ThrowIfDisposed();
            if (payload.IsEmpty)
            {
                throw new SerializationException(
                    "Snapshot payload is empty — cannot load an empty snapshot."
                );
            }

            using var _profile = TrecsProfiling.Start("SnapshotSerializer.LoadSnapshot");

            try
            {
                _buffer.ClearMemoryStream();
                _buffer.MemoryStream.Write(payload);
                _buffer.MemoryStream.Position = 0;
                return LoadFromInternalBuffer();
            }
            catch
            {
                _buffer.ResetForErrorRecovery();
                throw;
            }
        }

        SnapshotMetadata LoadFromInternalBuffer()
        {
            _buffer.StartRead();
            var metadata = _buffer.Read<SnapshotMetadata>("Metadata");
            _worldStateSerializer.DeserializeState(_buffer.Reader);
            _buffer.StopRead(verifySentinel: true);
            return metadata;
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
                var metadata = _buffer.Read<SnapshotMetadata>("Metadata");
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
            _payloadPool.Clear();
#if DEBUG
            _payloadPoolSet.Clear();
#endif
            _buffer.Dispose();
        }
    }
}
