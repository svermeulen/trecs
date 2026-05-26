using System;

namespace Trecs.Internal
{
    /// <summary>
    /// Stateful collaborator shared by <see cref="BundleRecorder"/> (runtime)
    /// and <see cref="TrecsRewindBuffer"/> (editor) to consolidate the lifecycle
    /// and IO primitives both recorders need: an <see cref="WorldAccessor"/>
    /// with <see cref="AccessorRole.Unrestricted"/>, a reusable
    /// <see cref="SerializationBuffer"/> for input-queue serialization,
    /// the <see cref="OnFixedUpdateCompleted"/> subscription helper, and the
    /// input-history-locker add/remove pair.
    ///
    /// Intentionally narrow: capture *cadence* and *policy* (when to anchor,
    /// when to checksum, whether to cap, what
    /// <see cref="IInputHistoryLocker.MaxClearFrame"/> to report) stay in the
    /// owning recorder. The two recorders differ on those (BundleRecorder has
    /// one anchor cadence and no capacity cap; TrecsRewindBuffer has a second
    /// scrub-cache cadence plus drop-oldest caps) and folding them in here
    /// would mean callbacks-and-options. The lifecycle/IO bits don't differ
    /// and that's all this collaborator owns.
    ///
    /// Main-thread only; not thread-safe. One core per recorder.
    /// </summary>
    internal sealed class RecorderEngine : IDisposable
    {
        readonly World _world;
        readonly SerializerRegistry _serializerRegistry;
        readonly SnapshotSerializer _snapshotSerializer;

        readonly WorldAccessor _accessor;

        // Reused across queue-serialization calls so the backing MemoryStream
        // / writer buffers survive the recorder's lifetime — successive
        // recording sessions on the same recorder have roughly stable
        // sizes, so this avoids growing the LOH byte[] every time.
        readonly SerializationBuffer _queueBuffer;
        bool _disposed;

        public RecorderEngine(
            World world,
            SerializerRegistry serializerRegistry,
            SnapshotSerializer snapshotSerializer,
            string accessorLabel
        )
        {
            if (world == null)
                throw new ArgumentNullException(nameof(world));
            if (serializerRegistry == null)
                throw new ArgumentNullException(nameof(serializerRegistry));
            if (snapshotSerializer == null)
                throw new ArgumentNullException(nameof(snapshotSerializer));
            if (accessorLabel == null)
                throw new ArgumentNullException(nameof(accessorLabel));

            _world = world;
            _serializerRegistry = serializerRegistry;
            _snapshotSerializer = snapshotSerializer;
            _accessor = _world.CreateAccessor(AccessorRole.Unrestricted, accessorLabel);
            _queueBuffer = new SerializationBuffer(_serializerRegistry);
        }

        public World World => _world;
        public WorldAccessor Accessor => _accessor;
        public SnapshotSerializer SnapshotSerializer => _snapshotSerializer;

        /// <summary>
        /// Compute the current world-state checksum. Serializes a full
        /// snapshot into the internal buffer and hashes the result,
        /// consistent with checksums produced by
        /// <see cref="SnapshotSerializer.SaveSnapshot(int, out ReadOnlyMemory{byte}, out ulong, bool)"/>.
        /// </summary>
        public ulong ComputeChecksum(int version)
        {
            return _snapshotSerializer.ComputeChecksum(version, includeTypeChecks: true);
        }

        /// <summary>
        /// Capture an initial world snapshot bytes + checksum together. Used
        /// by <see cref="BundleRecorder.Start"/> to fill the bundle's
        /// <c>InitialSnapshot</c> bytes and the start-frame entry in the
        /// bundle's <c>Checksums</c> dict.
        /// The pool may hand back an oversized buffer; the returned
        /// <see cref="ReadOnlyMemory{T}"/> slice is sized to the exact valid
        /// range. The caller takes ownership of the payload buffer — the core
        /// does not return it to the pool.
        /// </summary>
        public void CaptureInitialState(
            int version,
            out ReadOnlyMemory<byte> payload,
            out ulong checksum
        )
        {
            _snapshotSerializer.SaveSnapshot(
                version,
                out payload,
                out checksum,
                includeTypeChecks: true
            );
        }

        /// <summary>
        /// Serialize the world's <c>EntityInputQueue</c> into a fresh
        /// <c>byte[]</c>. The reused queue buffer is cleared first so a shorter
        /// queue this call than the previous one doesn't carry stale trailing
        /// bytes into <c>ToArray</c>. On exception the buffer is forced back
        /// to Idle so subsequent calls can safely reuse it.
        /// </summary>
        public byte[] SerializeEntityInputQueue(int version)
        {
            _queueBuffer.ClearMemoryStream();
            try
            {
                _queueBuffer.StartWrite(version: version, includeTypeChecks: true);
                _accessor.GetEntityInputQueue().Serialize(_queueBuffer.Writer);
                _queueBuffer.EndWrite();
                return _queueBuffer.MemoryStream.ToArray();
            }
            catch
            {
                _queueBuffer.ResetForErrorRecovery();
                throw;
            }
        }

        /// <summary>
        /// Deserialize bundled input-queue bytes back into the world's
        /// <c>EntityInputQueue</c>. Counterpart to
        /// <see cref="SerializeEntityInputQueue"/>, used by the editor
        /// recorder's load-from-file path. On exception the buffer is
        /// forced back to Idle.
        /// </summary>
        public void DeserializeEntityInputQueue(ReadOnlySpan<byte> bytes)
        {
            _queueBuffer.ClearMemoryStream();
            try
            {
                _queueBuffer.MemoryStream.Write(bytes);
                _queueBuffer.MemoryStream.Position = 0;
                _queueBuffer.StartRead();
                _accessor.GetEntityInputQueue().Deserialize(_queueBuffer.Reader);
                // verifySentinel: false matches the original call sites —
                // queue payloads embedded in bundles don't carry the
                // top-level sentinel.
                _queueBuffer.StopRead(verifySentinel: false);
            }
            catch
            {
                _queueBuffer.ResetForErrorRecovery();
                throw;
            }
        }

        /// <summary>
        /// Force the queue buffer back to Idle. Called from recover paths
        /// where a step *outside* the try block in
        /// <see cref="SerializeEntityInputQueue"/> /
        /// <see cref="DeserializeEntityInputQueue"/> faults and a subsequent
        /// use of the buffer would otherwise assert.
        /// </summary>
        public void ResetQueueBufferForErrorRecovery()
        {
            _queueBuffer?.ResetForErrorRecovery();
        }

        /// <summary>
        /// Subscribe to the world's <c>OnFixedUpdateCompleted</c> event via
        /// the cached accessor. Returns the subscription token; callers
        /// dispose it to detach.
        /// </summary>
        public IDisposable SubscribeFixedUpdateCompleted(Action handler)
        {
            return _accessor.Events.OnFixedUpdateCompleted(handler);
        }

        /// <summary>
        /// Add <paramref name="locker"/> to the world's
        /// <c>EntityInputQueue</c> as a history locker. No-op if the world
        /// has been disposed.
        /// </summary>
        public void AddHistoryLocker(IInputHistoryLocker locker)
        {
            if (_world.IsDisposed)
                return;
            _accessor.GetEntityInputQueue().AddHistoryLocker(locker);
        }

        /// <summary>
        /// Remove <paramref name="locker"/> from the world's
        /// <c>EntityInputQueue</c>. No-op if the world has been disposed.
        /// </summary>
        public void RemoveHistoryLocker(IInputHistoryLocker locker)
        {
            if (_world.IsDisposed)
                return;
            _accessor.GetEntityInputQueue().RemoveHistoryLocker(locker);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _queueBuffer.Dispose();
            // Accessor + calculator have no IDisposable surface.
        }
    }
}
