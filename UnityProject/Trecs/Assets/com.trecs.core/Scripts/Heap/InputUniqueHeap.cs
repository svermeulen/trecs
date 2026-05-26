using System.Collections.Generic;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Heap that owns managed input-allocated objects (<see cref="InputUniquePtr{T}"/>).
    /// Each allocation spawns from <see cref="ITrecsPoolManager"/> and is despawned
    /// back to the pool when the allocating frame is trimmed.
    ///
    /// <para>Per-frame bucketing (frame → list-of-handles) makes
    /// <c>ClearAtOrBeforeFrame</c> O(frames-trimmed × handles-per-frame); per-frame
    /// <see cref="List{}"/>s are pooled to avoid GC churn on high-churn input
    /// workloads. The handle-to-object map is a separate
    /// <see cref="IterableDictionary{,Entry}"/> for deterministic iteration in
    /// <c>ClearAll</c> and serialization paths.</para>
    /// </summary>
    public sealed class InputUniqueHeap
    {
        readonly TrecsLog _log;
        readonly ITrecsPoolManager _poolManager;

        readonly IterableDictionary<uint, Entry> _entries = new();
        readonly IterableDictionary<int, List<uint>> _handlesByFrame = new();
        readonly Stack<List<uint>> _listPool = new();
        readonly List<int> _frameRemoveBuffer = new();

        // Skip 0 — PtrHandle reserves 0 as the null sentinel.
        uint _nextHandleId = 1;

        bool _isDisposed;

        public InputUniqueHeap(TrecsLog log, ITrecsPoolManager poolManager)
        {
            _log = log;
            _poolManager = poolManager;
        }

        public int NumLiveAllocations
        {
            get
            {
                TrecsDebugAssert.That(!_isDisposed);
                return _entries.Count;
            }
        }

        internal InputUniquePtr<T> Alloc<T>(int frame, T value)
            where T : class
        {
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(frame >= 0);
            TrecsDebugAssert.IsNotNull(value);
            TrecsDebugAssert.That(_poolManager.HasPool<T>());
            TrecsDebugAssert.That(value.GetType() == typeof(T));

            var id = _nextHandleId++;
            _entries.Add(id, new Entry(value, TypeId<T>.Value, frame));
            TrackHandle(frame, id);
            _log.Trace(
                "Allocated input unique handle={0} type={1} frame={2}",
                id,
                typeof(T),
                frame
            );
            return new InputUniquePtr<T>(new PtrHandle(id));
        }

        internal InputUniquePtr<T> Alloc<T>(int frame)
            where T : class => Alloc<T>(frame, _poolManager.Spawn<T>());

        internal T ResolveValue<T>(PtrHandle handle)
            where T : class
        {
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(!handle.IsNull);
            if (!_entries.TryGetValue(handle.Value, out var entry))
            {
                throw TrecsDebugAssert.CreateException(
                    "Attempted to resolve invalid InputUniquePtr handle {0}",
                    handle.Value
                );
            }
            TrecsDebugAssert.That(entry.TypeId == TypeId<T>.Value);
            TrecsDebugAssert.IsNotNull(entry.Value);
            return (T)entry.Value;
        }

        internal bool TryResolveValue<T>(PtrHandle handle, out T value)
            where T : class
        {
            TrecsDebugAssert.That(!_isDisposed);
            if (handle.IsNull)
            {
                value = null;
                return false;
            }
            if (_entries.TryGetValue(handle.Value, out var entry))
            {
                TrecsDebugAssert.That(entry.TypeId == TypeId<T>.Value);
                value = (T)entry.Value;
                return true;
            }
            value = null;
            return false;
        }

        internal bool ContainsEntry(PtrHandle handle)
        {
            TrecsDebugAssert.That(!_isDisposed);
            return !handle.IsNull && _entries.ContainsKey(handle.Value);
        }

        void TrackHandle(int frame, uint id)
        {
            if (!_handlesByFrame.TryGetValue(frame, out var list))
            {
                list = _listPool.Count > 0 ? _listPool.Pop() : new List<uint>();
                TrecsDebugAssert.That(list.Count == 0);
                _handlesByFrame.Add(frame, list);
            }
            list.Add(id);
        }

        internal void ClearAtOrAfterFrame(int frame)
        {
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(_frameRemoveBuffer.IsEmpty());
            foreach (var (f, _) in _handlesByFrame)
            {
                if (f >= frame)
                {
                    _frameRemoveBuffer.Add(f);
                }
            }
            foreach (var f in _frameRemoveBuffer)
            {
                ReleaseFrame(f);
            }
            _frameRemoveBuffer.Clear();
        }

        internal void ClearAtOrBeforeFrame(int frame)
        {
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(_frameRemoveBuffer.IsEmpty());
            foreach (var (f, _) in _handlesByFrame)
            {
                if (f <= frame)
                {
                    _frameRemoveBuffer.Add(f);
                }
            }
            foreach (var f in _frameRemoveBuffer)
            {
                ReleaseFrame(f);
            }
            _frameRemoveBuffer.Clear();
        }

        internal void ClearAll()
        {
            TrecsDebugAssert.That(!_isDisposed);
            foreach (var (_, list) in _handlesByFrame)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    DisposeEntryById(list[i]);
                }
                list.Clear();
                _listPool.Push(list);
            }
            _handlesByFrame.Clear();
            TrecsDebugAssert.IsEqual(
                _entries.Count,
                0,
                "InputUniqueHeap entry leak after ClearAll"
            );
        }

        void ReleaseFrame(int frame)
        {
            var list = _handlesByFrame.RemoveAndGet(frame);
            for (int i = 0; i < list.Count; i++)
            {
                DisposeEntryById(list[i]);
            }
            list.Clear();
            _listPool.Push(list);
        }

        void DisposeEntryById(uint id)
        {
            var entry = _entries.RemoveAndGet(id);
            TrecsDebugAssert.IsNotNull(entry.Value);
            _poolManager.Despawn(entry.Value.GetType(), entry.Value);
        }

        /// <summary>
        /// Writes (id, frame, typeId, value) per entry. _handlesByFrame is
        /// rebuilt on Deserialize from the entries' frames; only _entries
        /// itself is on the wire. The handle counter round-trips by direct
        /// assignment — callers ClearAll before Deserialize so there's no
        /// live counter to preserve.
        /// </summary>
        internal void Serialize(ISerializationWriter writer)
        {
            TrecsDebugAssert.That(!_isDisposed);

            writer.Write<uint>("IdCounter", _nextHandleId);
            writer.Write<int>("NumEntries", _entries.Count);
            foreach (var (id, entry) in _entries)
            {
                writer.Write<uint>("Id", id);
                writer.Write<int>("Frame", entry.Frame);
                writer.Write<int>("TypeId", entry.TypeId.Value);
                TrecsDebugAssert.IsNotNull(entry.Value);
                writer.WriteObject("Value", entry.Value);
            }
        }

        internal void Deserialize(ISerializationReader reader)
        {
            TrecsDebugAssert.That(!_isDisposed);
            // Defensive: callers contract is ClearAll() before Deserialize.
            ClearAll();

            _nextHandleId = reader.Read<uint>("IdCounter");
            var numEntries = reader.Read<int>("NumEntries");

            for (int i = 0; i < numEntries; i++)
            {
                var id = reader.Read<uint>("Id");
                var frame = reader.Read<int>("Frame");
                var typeId = new TypeId(reader.Read<int>("TypeId"));
                object obj = null;
                reader.ReadObject("Value", ref obj);
                TrecsDebugAssert.That(TypeId.FromType(obj.GetType()) == typeId);

                _entries.Add(id, new Entry(obj, typeId, frame));
                TrackHandle(frame, id);
            }
            _log.Debug("Deserialized {0} entries into InputUniqueHeap", _entries.Count);
        }

        internal void Dispose()
        {
            TrecsDebugAssert.That(!_isDisposed);
            ClearAll();
            _isDisposed = true;
        }

        readonly struct Entry
        {
            public readonly object Value;
            public readonly TypeId TypeId;
            public readonly int Frame;

            public Entry(object value, TypeId typeId, int frame)
            {
                Value = value;
                TypeId = typeId;
                Frame = frame;
            }
        }
    }
}
