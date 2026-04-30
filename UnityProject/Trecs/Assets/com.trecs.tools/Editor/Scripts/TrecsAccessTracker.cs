using System.Collections.Generic;
using Trecs;
using Trecs.Collections;
using Trecs.Internal;
using UnityEditor;

namespace Trecs.Tools
{
    /// <summary>
    /// Per-world implementation of <see cref="IComponentAccessRecorder"/> that
    /// dedupes incoming access events into four maps used by the inspectors:
    /// <list type="bullet">
    /// <item>component → set of writer system names,</item>
    /// <item>component → set of reader system names,</item>
    /// <item>system name → set of components written,</item>
    /// <item>system name → set of components read.</item>
    /// </list>
    /// Registered on the <see cref="World"/> by
    /// <see cref="TrecsAccessRegistry"/> when any debug window is open. The
    /// recorder fires on every component read/write, so dedupe is essential —
    /// otherwise allocations explode.
    /// </summary>
    public sealed class TrecsAccessTracker : IComponentAccessRecorder
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

        public IReadOnlyCollection<string> GetReadersOf(ComponentId id) =>
            _componentReaders.TryGetValue(id, out var s)
                ? (IReadOnlyCollection<string>)s
                : System.Array.Empty<string>();

        public IReadOnlyCollection<string> GetWritersOf(ComponentId id) =>
            _componentWriters.TryGetValue(id, out var s)
                ? (IReadOnlyCollection<string>)s
                : System.Array.Empty<string>();

        public IReadOnlyCollection<ComponentId> GetReadsBy(string systemName) =>
            _systemReads.TryGetValue(systemName ?? string.Empty, out var s)
                ? (IReadOnlyCollection<ComponentId>)s
                : System.Array.Empty<ComponentId>();

        public IReadOnlyCollection<ComponentId> GetWritesBy(string systemName) =>
            _systemWrites.TryGetValue(systemName ?? string.Empty, out var s)
                ? (IReadOnlyCollection<ComponentId>)s
                : System.Array.Empty<ComponentId>();

        public IReadOnlyCollection<GroupIndex> GetGroupsTouchedBy(string systemName) =>
            _systemGroups.TryGetValue(systemName ?? string.Empty, out var s)
                ? (IReadOnlyCollection<GroupIndex>)s
                : System.Array.Empty<GroupIndex>();

        public IReadOnlyCollection<string> GetSystemsTouchingGroup(GroupIndex group) =>
            _groupSystems.TryGetValue(group, out var s)
                ? (IReadOnlyCollection<string>)s
                : System.Array.Empty<string>();

        public void Clear()
        {
            _componentReaders.Clear();
            _componentWriters.Clear();
            _systemReads.Clear();
            _systemWrites.Clear();
            _systemGroups.Clear();
            _groupSystems.Clear();
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
                if (!world.IsDisposed)
                {
                    world.SetComponentAccessRecorder(null);
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
            world.SetComponentAccessRecorder(tracker);
        }
    }
}
