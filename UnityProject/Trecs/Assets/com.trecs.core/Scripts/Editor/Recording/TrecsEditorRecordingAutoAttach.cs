#pragma warning disable TRECS128

using System;
using System.Collections.Generic;

namespace Trecs.Internal
{
    /// <summary>
    /// Refcounted helper that constructs a <see cref="TrecsRewindBuffer"/> +
    /// <see cref="TrecsRecordingSession"/> pair for every active
    /// <see cref="World"/> while at least one <see cref="TrecsPlayerWindow"/>
    /// is open, and disposes them when the last window closes.
    ///
    /// User composition roots no longer need to wire these types up manually
    /// — the editor side discovers Worlds via <see cref="WorldRegistry"/> and
    /// uses each world's <see cref="World.SerializerRegistry"/> directly.
    ///
    /// When the Player window is closed there is zero editor overhead — no
    /// per-fixed-frame subscriptions, no scratch buffers, no accessor
    /// allocation.
    /// </summary>
    static class TrecsEditorRecordingAutoAttach
    {
        static readonly TrecsLog _log = TrecsLog.Default;

        static int _activationCount;
        static readonly Dictionary<World, AttachedSet> _attached = new();

        struct AttachedSet
        {
            public TrecsRewindBuffer Recorder;
            public TrecsRecordingSession Session;
            public TrecsSnapshotLibrary SnapshotLibrary;

            // SnapshotSerializer is IDisposable; we own the instance here so
            // we have to dispose it on detach. WorldStateSerializer is not
            // IDisposable so it doesn't need tracking.
            public SnapshotSerializer Snapshots;
        }

        /// <summary>
        /// Look up the snapshot library for <paramref name="world"/>, or
        /// null if no world is attached. Editor windows fetch the library
        /// through here rather than constructing their own — the lifetime
        /// is owned by the attach refcount.
        /// </summary>
        public static TrecsSnapshotLibrary GetSnapshotLibrary(World world)
        {
            if (world == null || !_attached.TryGetValue(world, out var attached))
            {
                return null;
            }
            return attached.SnapshotLibrary;
        }

        /// <summary>
        /// Called by <see cref="TrecsPlayerWindow.OnEnable"/>. First call wires
        /// up the WorldRegistry subscriptions and attaches to every currently
        /// active world; subsequent calls just bump the refcount so multiple
        /// open windows share one set of controllers.
        /// </summary>
        public static void Activate()
        {
            _activationCount++;
            if (_activationCount > 1)
            {
                return;
            }

            WorldRegistry.WorldRegistered += AttachForWorld;
            WorldRegistry.WorldUnregistered += DetachForWorld;
            foreach (var world in WorldRegistry.ActiveWorlds)
            {
                AttachForWorld(world);
            }
        }

        /// <summary>
        /// Called by <see cref="TrecsPlayerWindow.OnDisable"/>. Last call
        /// disposes every attached controller and unhooks the WorldRegistry
        /// subscriptions so no editor cost survives the window closing.
        /// </summary>
        public static void Deactivate()
        {
            TrecsDebugAssert.That(_activationCount > 0, "Deactivate without matching Activate");
            _activationCount--;
            if (_activationCount > 0)
            {
                return;
            }

            WorldRegistry.WorldRegistered -= AttachForWorld;
            WorldRegistry.WorldUnregistered -= DetachForWorld;
            foreach (var attached in _attached.Values)
            {
                attached.Session.Dispose();
                attached.Recorder.Dispose();
                attached.Snapshots.Dispose();
            }
            _attached.Clear();
        }

        static void AttachForWorld(World world)
        {
            if (_attached.ContainsKey(world))
            {
                return;
            }

            var registry = world.SerializerRegistry;
            var stateSerializer = new WorldStateSerializer(world);
            SnapshotSerializer snapshots = null;
            TrecsRewindBuffer recorder = null;
            TrecsRecordingSession session = null;
            try
            {
                var pool = new SnapshotPayloadPool();
                snapshots = new SnapshotSerializer(stateSerializer, registry, world, pool);
                recorder = new TrecsRewindBuffer(
                    world,
                    stateSerializer,
                    registry,
                    new TrecsRewindBufferSettings(),
                    snapshots,
                    pool
                );
                // Constructor fires TrecsRecordingSessionRegistry.ControllerRegistered,
                // which TrecsGameStateActivator listens to so that auto-record-on-open
                // kicks in without any extra wiring from us.
                session = new TrecsRecordingSession(world, recorder);
            }
            catch (Exception e)
            {
                // Partial init — dispose whatever did construct. Swallow
                // further failures so a Dispose-throw doesn't mask the
                // original error.
                try
                {
                    session?.Dispose();
                }
                catch { }
                try
                {
                    recorder?.Dispose();
                }
                catch { }
                try
                {
                    snapshots?.Dispose();
                }
                catch { }
                _log.Error("Failed to auto-attach Trecs Player to world {0}: {1}", world, e);
                return;
            }
            // SnapshotLibrary depends on a fully-constructed session (it
            // calls Stop/Start on it during LoadSnapshot), so construct it
            // last. Stateless apart from its dependency refs.
            var snapshotLibrary = new TrecsSnapshotLibrary(world, snapshots, session);
            _attached[world] = new AttachedSet
            {
                Recorder = recorder,
                Session = session,
                SnapshotLibrary = snapshotLibrary,
                Snapshots = snapshots,
            };
            _log.Trace("Auto-attached Trecs Player to world {0}", world);
        }

        static void DetachForWorld(World world)
        {
            if (!_attached.TryGetValue(world, out var attached))
            {
                return;
            }
            _attached.Remove(world);
            attached.Session.Dispose();
            attached.Recorder.Dispose();
            attached.Snapshots.Dispose();
            _log.Trace("Auto-detached Trecs Player from world {0}", world);
        }
    }
}
