using System.Collections.Generic;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Frame-scoped heap that owns managed objects via <see cref="UniquePtr{T}"/>.
    /// Entries are tagged with the frame they were allocated on and can be bulk-cleared
    /// by frame range, supporting rollback and replay scenarios.
    /// </summary>
    public sealed class FrameScopedUniqueHeap
    {
        readonly TrecsLog _log;

        readonly DenseDictionary<uint, HeapEntry> _entries = new();
        readonly ITrecsPoolManager _poolManager;
        readonly List<uint> _removeBuffer = new();
        readonly HeapIdCounter _idCounter = new(2, 2);

        bool _isDisposed;

        public FrameScopedUniqueHeap(TrecsLog log, ITrecsPoolManager poolManager)
        {
            _log = log;
            _poolManager = poolManager;
        }

        public int NumEntries
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return _entries.Count;
            }
        }

        internal UniquePtr<T> Alloc<T>(int frame, T value)
            where T : class
        {
            TrecsAssert.That(!_isDisposed);
            TrecsAssert.IsNotNull(value);

            TrecsAssert.That(_poolManager.HasPool<T>());

            TrecsAssert.That(value.GetType() == typeof(T));

            var typeId = TypeIdProvider.GetTypeId<T>();
            var id = _idCounter.Alloc();
            _entries.Add(id, new HeapEntry(value, typeId, frame));
            _log.Trace("Allocated new input pointer with id {0} and type {1}", id, typeof(T));
            return new UniquePtr<T>(new PtrHandle(id));
        }

        internal UniquePtr<T> Alloc<T>(int frame)
            where T : class
        {
            TrecsAssert.That(!_isDisposed);
            return Alloc<T>(frame, _poolManager.Spawn<T>());
        }

        internal void ClearAtOrAfterFrame(int frame)
        {
            TrecsAssert.That(!_isDisposed);

            TrecsAssert.That(_removeBuffer.IsEmpty());
            _removeBuffer.Clear();

            foreach (var (key, value) in _entries)
            {
                if (value.Frame >= frame)
                {
                    _removeBuffer.Add(key);
                }
            }

            foreach (var key in _removeBuffer)
            {
                DisposeEntry(key);
            }

            _removeBuffer.Clear();
        }

        // Linear scan is acceptable here: called every frame, but the entry count
        // is typically very small (single digits). A sorted structure would add
        // complexity without measurable benefit in practice.
        internal void ClearAtOrBeforeFrame(int frame)
        {
            TrecsAssert.That(!_isDisposed);

            TrecsAssert.That(_removeBuffer.IsEmpty());
            _removeBuffer.Clear();

            foreach (var (key, value) in _entries)
            {
                if (value.Frame <= frame)
                {
                    _removeBuffer.Add(key);
                }
            }

            foreach (var key in _removeBuffer)
            {
                DisposeEntry(key);
            }

            _removeBuffer.Clear();
        }

        HeapEntry GetEntry(int frame, uint address)
        {
            TrecsAssert.That(!_isDisposed);

            if (_entries.TryGetValue(address, out var entry))
            {
                TrecsAssert.IsEqual(
                    entry.Frame,
                    frame,
                    "Attempted to get input memory for different frame than it was allocated for"
                );
                return entry;
            }

            throw TrecsAssert.CreateException(
                "Attempted to get invalid heap memory address ({0}) for frame {1}",
                address,
                frame
            );
        }

        internal T ResolveValue<T>(int frame, uint address)
            where T : class
        {
            var entry = GetEntry(frame, address);
            TrecsAssert.That(entry.TypeId == TypeIdProvider.GetTypeId<T>());
            TrecsAssert.IsNotNull(entry.Value);
            return (T)entry.Value;
        }

        internal bool TryResolveValue<T>(uint address, int frame, out T value)
            where T : class
        {
            TrecsAssert.That(!_isDisposed);

            if (_entries.TryGetValue(address, out var entry))
            {
                TrecsAssert.IsEqual(
                    entry.Frame,
                    frame,
                    "Attempted to get input memory for different frame than it was allocated for"
                );
                TrecsAssert.That(entry.TypeId == TypeIdProvider.GetTypeId<T>());
                TrecsAssert.IsNotNull(entry.Value);
                value = (T)entry.Value;
                return true;
            }

            value = default;
            return false;
        }

        internal bool ContainsEntry(uint address)
        {
            TrecsAssert.That(!_isDisposed);
            return _entries.ContainsKey(address);
        }

        internal void Dispose()
        {
            TrecsAssert.That(!_isDisposed);
            ClearAll();
            _isDisposed = true;
        }

        internal void ClearAll()
        {
            TrecsAssert.That(!_isDisposed);
            foreach (var (_, entry) in _entries)
            {
                TrecsAssert.IsNotNull(entry.Value);
                _poolManager.Despawn(entry.Value.GetType(), entry.Value);
            }
            _entries.Clear();
        }

        internal void DisposeEntry(uint address)
        {
            TrecsAssert.That(!_isDisposed);
            if (!_entries.TryRemove(address, out var entry))
            {
                throw TrecsAssert.CreateException(
                    "Attempted to dispose invalid heap memory address ({0})",
                    address
                );
            }

            TrecsAssert.IsNotNull(entry.Value);
            var entryType = entry.Value.GetType();
            _poolManager.Despawn(entryType, entry.Value);

            _log.Trace("Disposed input ptr with address {0} and type {1}", address, entryType);
        }

        internal void Serialize(ISerializationWriter writer)
        {
            TrecsAssert.That(!_isDisposed);

            writer.Write<uint>("IdCounter", _idCounter.Value);
            writer.Write<int>("NumEntries", _entries.Count);

            foreach (var (address, entry) in _entries)
            {
                writer.Write<uint>("Address", address);
                writer.Write<int>("Frame", entry.Frame);
                writer.Write<int>("TypeId", entry.TypeId);

                TrecsAssert.IsNotNull(entry.Value);
                writer.WriteObject("Value", entry.Value);
            }
        }

        internal void Deserialize(ISerializationReader reader)
        {
            TrecsAssert.That(!_isDisposed);

            TrecsAssert.That(_entries.Count == 0);

            // See FrameScopedSharedHeap.Deserialize for the rationale behind EnsureAtLeast.
            _idCounter.EnsureAtLeast(reader.Read<uint>("IdCounter"));
            var numEntries = reader.Read<int>("NumEntries");

            _entries.EnsureCapacity(numEntries);

            uint maxAddress = 0;

            for (int i = 0; i < numEntries; i++)
            {
                var address = reader.Read<uint>("Address");
                if (address > maxAddress)
                {
                    maxAddress = address;
                }

                var frame = reader.Read<int>("Frame");
                var typeId = reader.Read<int>("TypeId");

                object obj = null;
                reader.ReadObject("Value", ref obj);

                var objType = obj.GetType();
                TrecsAssert.That(TypeIdProvider.GetTypeId(objType) == typeId);

                _entries.Add(address, new HeapEntry(obj, typeId, frame));

                _log.Trace(
                    "Deserialized dynamic pointer with id {0} and type {1}",
                    address,
                    objType
                );
            }

            if (maxAddress > 0)
            {
                _idCounter.AdvancePast(maxAddress);
            }

            _log.Debug("Deserialized {0} input heap entries", _entries.Count);
        }

        readonly struct HeapEntry
        {
            public readonly object Value;
            public readonly int TypeId;
            public readonly int Frame;

            public HeapEntry(object value, int typeId, int frame)
            {
                Value = value;
                TypeId = typeId;
                Frame = frame;
            }
        }
    }
}
