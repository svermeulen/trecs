using System;
using System.Collections.Generic;
using Trecs.Internal;
using UnityEditor;

namespace Trecs
{
    /// <summary>
    /// Per-world implementation of <see cref="IAccessRecorder"/> that
    /// dedupes incoming access events into maps used by the inspectors:
    /// <list type="bullet">
    /// <item>component → set of writer system names,</item>
    /// <item>component → set of reader system names,</item>
    /// <item>system name → set of components written,</item>
    /// <item>system name → set of components read,</item>
    /// <item>system ↔ groups added / removed / moved.</item>
    /// </list>
    /// Registered on the <see cref="World"/> by
    /// <see cref="TrecsAccessRegistry"/> when any debug window is open. The
    /// component recorder fires on every read/write, so dedupe is essential —
    /// otherwise allocations explode.
    /// </summary>
    public sealed class TrecsAccessTracker : IAccessRecorder
    {
        readonly Dictionary<ComponentId, HashSet<string>> _componentWriters = new();
        readonly Dictionary<ComponentId, HashSet<string>> _componentReaders = new();
        readonly Dictionary<string, HashSet<ComponentId>> _systemWrites = new();
        readonly Dictionary<string, HashSet<ComponentId>> _systemReads = new();

        // Tag-list derivation in the tag inspector needs the inverse — the
        // recorder fires per (system, group, component), so we keep the
        // system→groups map here. Same dedupe pattern.
        readonly Dictionary<string, HashSet<GroupIndex>> _systemGroups = new();
        readonly Dictionary<GroupIndex, HashSet<string>> _groupSystems = new();

        readonly Dictionary<string, HashSet<GroupIndex>> _systemAdds = new();
        readonly Dictionary<string, HashSet<GroupIndex>> _systemRemoves = new();
        readonly Dictionary<string, HashSet<GroupIndex>> _systemMoves = new();
        readonly Dictionary<GroupIndex, HashSet<string>> _groupAdders = new();
        readonly Dictionary<GroupIndex, HashSet<string>> _groupRemovers = new();
        readonly Dictionary<GroupIndex, HashSet<string>> _groupMovers = new();

        public void OnComponentAccess(
            string systemName,
            GroupIndex group,
            ComponentId componentType,
            bool isReadOnly
        )
        {
            if (string.IsNullOrEmpty(systemName))
            {
                return;
            }
            if (isReadOnly)
            {
                Add(_componentReaders, componentType, systemName);
                Add(_systemReads, systemName, componentType);
            }
            else
            {
                Add(_componentWriters, componentType, systemName);
                Add(_systemWrites, systemName, componentType);
            }
            Add(_systemGroups, systemName, group);
            Add(_groupSystems, group, systemName);
        }

        public void OnEntityAdded(string systemName, GroupIndex group)
        {
            if (string.IsNullOrEmpty(systemName))
            {
                return;
            }
            Add(_systemAdds, systemName, group);
            Add(_groupAdders, group, systemName);
            Add(_systemGroups, systemName, group);
            Add(_groupSystems, group, systemName);
        }

        public void OnEntityRemoved(string systemName, GroupIndex group)
        {
            if (string.IsNullOrEmpty(systemName))
            {
                return;
            }
            Add(_systemRemoves, systemName, group);
            Add(_groupRemovers, group, systemName);
            Add(_systemGroups, systemName, group);
            Add(_groupSystems, group, systemName);
        }

        // Per design: a move flags BOTH the source and destination groups
        // under "moves" (so the source shows "moves to" and the destination
        // shows "moved to by" the same system).
        public void OnEntityMoved(string systemName, GroupIndex fromGroup, GroupIndex toGroup)
        {
            if (string.IsNullOrEmpty(systemName))
            {
                return;
            }
            Add(_systemMoves, systemName, fromGroup);
            Add(_systemMoves, systemName, toGroup);
            Add(_groupMovers, fromGroup, systemName);
            Add(_groupMovers, toGroup, systemName);
            Add(_systemGroups, systemName, fromGroup);
            Add(_systemGroups, systemName, toGroup);
            Add(_groupSystems, fromGroup, systemName);
            Add(_groupSystems, toGroup, systemName);
        }

        public IReadOnlyCollection<string> GetReadersOf(ComponentId id) =>
            _componentReaders.TryGetValue(id, out var s)
                ? (IReadOnlyCollection<string>)s
                : Array.Empty<string>();

        public IReadOnlyCollection<string> GetWritersOf(ComponentId id) =>
            _componentWriters.TryGetValue(id, out var s)
                ? (IReadOnlyCollection<string>)s
                : Array.Empty<string>();

        public IReadOnlyCollection<ComponentId> GetReadsBy(string systemName) =>
            _systemReads.TryGetValue(systemName ?? string.Empty, out var s)
                ? (IReadOnlyCollection<ComponentId>)s
                : Array.Empty<ComponentId>();

        public IReadOnlyCollection<ComponentId> GetWritesBy(string systemName) =>
            _systemWrites.TryGetValue(systemName ?? string.Empty, out var s)
                ? (IReadOnlyCollection<ComponentId>)s
                : Array.Empty<ComponentId>();

        public IReadOnlyCollection<GroupIndex> GetGroupsTouchedBy(string systemName) =>
            _systemGroups.TryGetValue(systemName ?? string.Empty, out var s)
                ? (IReadOnlyCollection<GroupIndex>)s
                : Array.Empty<GroupIndex>();

        // Every accessor debug name the tracker has seen at least one
        // (component or group) access for. Used by TrecsSchemaCache to walk
        // accessors when persisting per-accessor data like tags-touched.
        public IReadOnlyCollection<string> GetAllTrackedAccessorNames() => _systemGroups.Keys;

        public IReadOnlyCollection<string> GetSystemsTouchingGroup(GroupIndex group) =>
            _groupSystems.TryGetValue(group, out var s)
                ? (IReadOnlyCollection<string>)s
                : Array.Empty<string>();

        public IReadOnlyCollection<GroupIndex> GetGroupsAddedBy(string systemName) =>
            _systemAdds.TryGetValue(systemName ?? string.Empty, out var s)
                ? (IReadOnlyCollection<GroupIndex>)s
                : Array.Empty<GroupIndex>();

        public IReadOnlyCollection<GroupIndex> GetGroupsRemovedBy(string systemName) =>
            _systemRemoves.TryGetValue(systemName ?? string.Empty, out var s)
                ? (IReadOnlyCollection<GroupIndex>)s
                : Array.Empty<GroupIndex>();

        public IReadOnlyCollection<GroupIndex> GetGroupsMovedBy(string systemName) =>
            _systemMoves.TryGetValue(systemName ?? string.Empty, out var s)
                ? (IReadOnlyCollection<GroupIndex>)s
                : Array.Empty<GroupIndex>();

        public IReadOnlyCollection<string> GetSystemsAddingTo(GroupIndex group) =>
            _groupAdders.TryGetValue(group, out var s)
                ? (IReadOnlyCollection<string>)s
                : Array.Empty<string>();

        public IReadOnlyCollection<string> GetSystemsRemovingFrom(GroupIndex group) =>
            _groupRemovers.TryGetValue(group, out var s)
                ? (IReadOnlyCollection<string>)s
                : Array.Empty<string>();

        public IReadOnlyCollection<string> GetSystemsMovingOn(GroupIndex group) =>
            _groupMovers.TryGetValue(group, out var s)
                ? (IReadOnlyCollection<string>)s
                : Array.Empty<string>();

        public void Clear()
        {
            _componentReaders.Clear();
            _componentWriters.Clear();
            _systemReads.Clear();
            _systemWrites.Clear();
            _systemGroups.Clear();
            _groupSystems.Clear();
            _systemAdds.Clear();
            _systemRemoves.Clear();
            _systemMoves.Clear();
            _groupAdders.Clear();
            _groupRemovers.Clear();
            _groupMovers.Clear();
        }

        static void Add<TKey, TVal>(Dictionary<TKey, HashSet<TVal>> map, TKey key, TVal value)
        {
            if (!map.TryGetValue(key, out var set))
            {
                set = new HashSet<TVal>();
                map[key] = set;
            }
            set.Add(value);
        }
    }

    /// <summary>
    /// Static editor-session registry that lazily attaches a
    /// <see cref="TrecsAccessTracker"/> to every active <see cref="World"/>
    /// and detaches on world unregistration. Inspectors call
    /// <see cref="GetTracker"/> to surface the data; tracker creation is
    /// idempotent. The registry is initialized on editor load via
    /// <see cref="InitializeOnLoadAttribute"/> so trackers exist from the
    /// moment a world starts running, even before any debug window opens.
    /// </summary>
    [InitializeOnLoad]
    public static class TrecsAccessRegistry
    {
        static readonly Dictionary<World, TrecsAccessTracker> _trackers = new();

        // Fires inside OnWorldUnregistered, before the tracker is detached
        // and cleared. Subscribers (e.g. TrecsSchemaCache) can read the
        // tracker's accumulated data here and persist it — using the plain
        // WorldRegistry.WorldUnregistered event would race with this class's
        // own handler, since [InitializeOnLoad] subscription order is
        // implementation-defined.
        public static event Action<World> WorldAccessTrackerWillClear;

        static TrecsAccessRegistry()
        {
            WorldRegistry.WorldRegistered += OnWorldRegistered;
            WorldRegistry.WorldUnregistered += OnWorldUnregistered;
            // Attach to any worlds already alive at editor load (domain reload
            // mid-play, or later registration of this static).
            foreach (var w in WorldRegistry.ActiveWorlds)
            {
                Attach(w);
            }
        }

        public static TrecsAccessTracker GetTracker(World world)
        {
            if (world == null)
            {
                return null;
            }
            return _trackers.TryGetValue(world, out var t) ? t : null;
        }

        static void OnWorldRegistered(World world) => Attach(world);

        static void OnWorldUnregistered(World world)
        {
            if (_trackers.TryGetValue(world, out var existing))
            {
                try
                {
                    WorldAccessTrackerWillClear?.Invoke(world);
                }
                catch (Exception)
                {
                    // A subscriber blowing up shouldn't strand the world
                    // with a live recorder pointer.
                }
                if (!world.IsDisposed)
                {
                    world.SetAccessRecorder(null);
                }
                existing.Clear();
                _trackers.Remove(world);
            }
        }

        static void Attach(World world)
        {
            if (world == null || world.IsDisposed || _trackers.ContainsKey(world))
            {
                return;
            }
            var tracker = new TrecsAccessTracker();
            _trackers[world] = tracker;
            world.SetAccessRecorder(tracker);
        }
    }
}
