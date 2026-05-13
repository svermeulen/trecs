using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Trecs.Collections;

namespace Trecs.Internal
{
    /// <summary>
    /// Implementation Notes:
    /// - We need to use the serialization manager type id instead of burst type hash, or .net type hash, because
    ///   this value needs to be persistent across runs, and these latter ones use things like assembly name
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class UniqueHeap
    {
        readonly TrecsLog _log;

        readonly DenseDictionary<uint, HeapEntry> _entries = new();
        readonly ITrecsPoolManager _poolManager;

        readonly HeapIdCounter _idCounter = new(1, 2);
        bool _isDisposed;

        internal UniqueHeap(TrecsLog log, ITrecsPoolManager poolManager)
        {
            _log = log;
            _poolManager = poolManager;
        }

        public int NumEntries
        {
            get
            {
                Assert.That(!_isDisposed);
                return _entries.Count;
            }
        }

        internal UniquePtr<T> AllocUnique<T>(T value)
            where T : class
        {
            Assert.That(!_isDisposed);

            var id = _idCounter.Alloc();
            var entry = new HeapEntry(value, typeof(T));
            _entries.Add(id, entry);
            _log.Trace("Allocated new dynamic pointer with id {0} and type {1}", id, typeof(T));
            return new UniquePtr<T>(new PtrHandle(id));
        }

        internal UniquePtr<T> AllocUnique<T>()
            where T : class
        {
            Assert.That(!_isDisposed);

            if (_poolManager != null)
            {
                return AllocUnique<T>(_poolManager.Spawn<T>());
            }

            return AllocUnique<T>(Activator.CreateInstance<T>());
        }

        internal void SetEntry<T>(uint address, T value)
        {
            Assert.That(!_isDisposed);
            Assert.That(address != 0);

            if (_entries.TryGetValue(address, out var entry))
            {
                Assert.That(
                    entry.Type == typeof(T),
                    "Attempted to set heap memory address ({}) of type {} with value of incompatible type {}",
                    address,
                    entry.Type,
                    typeof(T)
                );

                if (!ReferenceEquals(entry.Value, value))
                {
                    if (entry.Value != null && _poolManager != null)
                    {
                        _poolManager.Despawn(entry.Type, entry.Value);
                    }

                    entry.Value = value;
                    _entries[address] = entry;
                }
            }
            else
            {
                throw Assert.CreateException(
                    "Attempted to set invalid heap memory address ({}) for type {}",
                    address,
                    typeof(T)
                );
            }
        }

        internal T GetEntry<T>(uint address)
        {
            if (TryGetEntry(address, out var entry))
            {
                Assert.That(
                    entry.Type == typeof(T),
                    "Expected heap memory address ({}) to be of type {}, but found type {}",
                    address,
                    typeof(T),
                    entry.Type
                );
                return (T)entry.Value;
            }

            throw Assert.CreateException(
                "Attempted to resolve invalid heap memory address ({}) for type {}",
                address,
                typeof(T)
            );
        }

        internal bool TryGetEntry(uint address, out HeapEntry entry)
        {
            Assert.That(!_isDisposed);
            return _entries.TryGetValue(address, out entry);
        }

        public object TryGetPtrValue(uint address)
        {
            Assert.That(!_isDisposed);

            if (TryGetEntry(address, out var entry))
            {
                return entry.Value;
            }

            return null;
        }

        public object GetPtrValue(uint address)
        {
            Assert.That(!_isDisposed);

            var result = TryGetPtrValue(address);
            Assert.IsNotNull(result, "Invalid address provided: {}", address);
            return result;
        }

        internal void Dispose()
        {
            Assert.That(!_isDisposed);
            // Explicit dispose on world shutdown catches leaked heap pointers (warns about undisposed entries).
            // Serialization/rollback paths handle cleanup automatically as bulk operations.
            ClearAll(warnUndisposed: true);
            _isDisposed = true;
        }

        internal void ClearAll(bool warnUndisposed)
        {
            Assert.That(!_isDisposed);

            if (warnUndisposed && _entries.Count > 0 && _log.IsWarningEnabled())
            {
                var allTypes = new HashSet<Type>();
                foreach (var (_, value) in _entries)
                {
                    allTypes.Add(value.Type);
                }
                _log.Warning(
                    "Found {0} undisposed dynamic entries in UniqueHeap with types:\n - {1}",
                    _entries.Count,
                    allTypes.Select(x => x.FullName).Join("\n - ")
                );
            }

            if (_poolManager != null)
            {
                foreach (var (_, entry) in _entries)
                {
                    if (entry.Value != null)
                    {
                        Assert.That(
                            _poolManager.HasPool(entry.Type),
                            "Found non-null pointer value for type {} with no associated memory pool",
                            entry.Type
                        );

                        _poolManager.Despawn(entry.Type, entry.Value);
                    }
                }
            }

            _entries.Clear();
        }

        internal void DisposeEntry<T>(uint address)
            where T : class
        {
            Assert.That(!_isDisposed);

            Assert.That(address != 0);

            if (_entries.TryRemove(address, out var entry))
            {
                if (entry.Value != null && _poolManager != null)
                {
                    _poolManager.Despawn(entry.Type, entry.Value);
                }

                _log.Trace(
                    "Disposed dynamic ptr with address {0} and type {1}",
                    address,
                    entry.Type
                );
            }
            else
            {
                throw Assert.CreateException(
                    "Attempted to dispose invalid heap memory address ({}) for type {}",
                    address,
                    typeof(T)
                );
            }
        }

        internal void Serialize(ITrecsSerializationWriter writer)
        {
            writer.Write<uint>("IdCounter", _idCounter.Value);
            writer.Write<int>("NumEntries", _entries.Count);

            foreach (var (address, entry) in _entries)
            {
                writer.Write<uint>("Address", address);

                Assert.That(entry.Type == entry.Value.GetType());
                writer.WriteObject("Obj", entry.Value);
            }
        }

        internal void Deserialize(ITrecsSerializationReader reader)
        {
            Assert.That(_entries.Count == 0);

            _idCounter.Value = reader.Read<uint>("IdCounter");

            var numDynamic = reader.Read<int>("NumEntries");
            _entries.EnsureCapacity(numDynamic);

            for (int i = 0; i < numDynamic; i++)
            {
                var address = reader.Read<uint>("Address");

                object obj = null;
                reader.ReadObject("Obj", ref obj);

                var objType = obj.GetType();
                var heapEntry = new HeapEntry(obj, objType);

                _entries.Add(address, heapEntry);

                _log.Trace(
                    "Deserialized dynamic pointer with id {0} and type {1}",
                    address,
                    objType
                );
            }
        }

        internal struct HeapEntry
        {
            public object Value;
            public readonly Type Type;

            public HeapEntry(object value, Type type)
            {
                Value = value;
                Type = type;
            }
        }
    }
}
