using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Trecs.Collections;

namespace Trecs.Internal
{
    // Uses TypeId (serialization-stable) instead of Burst/CLR type hashes
    // because the value must be persistent across application runs.
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class UniqueHeap
    {
        readonly TrecsLog _log;

        readonly IterableDictionary<uint, HeapEntry> _entries = new();
        readonly ITrecsPoolManager _poolManager;

        // Skip 0 — PtrHandle reserves 0 as the null sentinel.
        uint _nextHandleId = 1;
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
                TrecsDebugAssert.That(!_isDisposed);
                return _entries.Count;
            }
        }

        internal UniquePtr<T> AllocUnique<T>(T value)
            where T : class
        {
            TrecsDebugAssert.That(!_isDisposed);

            var id = _nextHandleId++;
            var entry = new HeapEntry(value, typeof(T));
            _entries.Add(id, entry);
            _log.Trace("Allocated new dynamic pointer with id {0} and type {1}", id, typeof(T));
            return new UniquePtr<T>(new PtrHandle(id));
        }

        internal UniquePtr<T> AllocUnique<T>()
            where T : class
        {
            TrecsDebugAssert.That(!_isDisposed);

            if (_poolManager != null)
            {
                return AllocUnique<T>(_poolManager.Spawn<T>());
            }

            return AllocUnique<T>(Activator.CreateInstance<T>());
        }

        internal void SetEntry<T>(uint address, T value)
        {
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(address != 0);

            if (_entries.TryGetValue(address, out var entry))
            {
                TrecsDebugAssert.That(
                    entry.Type == typeof(T),
                    "Attempted to set heap memory address ({0}) of type {1} with value of incompatible type {2}",
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
                throw TrecsDebugAssert.CreateException(
                    "Attempted to set invalid heap memory address ({0}) for type {1}",
                    address,
                    typeof(T)
                );
            }
        }

        internal T GetEntry<T>(uint address)
        {
            if (TryGetEntry(address, out var entry))
            {
                TrecsDebugAssert.That(
                    entry.Type == typeof(T),
                    "Expected heap memory address ({0}) to be of type {1}, but found type {2}",
                    address,
                    typeof(T),
                    entry.Type
                );
                return (T)entry.Value;
            }

            throw TrecsDebugAssert.CreateException(
                "Attempted to resolve invalid heap memory address ({0}) for type {1}",
                address,
                typeof(T)
            );
        }

        internal bool TryGetEntry(uint address, out HeapEntry entry)
        {
            TrecsDebugAssert.That(!_isDisposed);
            return _entries.TryGetValue(address, out entry);
        }

        public object TryGetPtrValue(uint address)
        {
            TrecsDebugAssert.That(!_isDisposed);

            if (TryGetEntry(address, out var entry))
            {
                return entry.Value;
            }

            return null;
        }

        public object GetPtrValue(uint address)
        {
            TrecsDebugAssert.That(!_isDisposed);

            var result = TryGetPtrValue(address);
            TrecsDebugAssert.IsNotNull(result, "Invalid address provided: {0}", address);
            return result;
        }

        internal void Dispose()
        {
            TrecsDebugAssert.That(!_isDisposed);
            ClearAll(warnUndisposed: true);
            _isDisposed = true;
        }

        internal void ClearAll(bool warnUndisposed)
        {
            TrecsDebugAssert.That(!_isDisposed);

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
                    string.Join("\n - ", allTypes.Select(x => x.FullName).ToArray())
                );
            }

            if (_poolManager != null)
            {
                foreach (var (_, entry) in _entries)
                {
                    if (entry.Value != null)
                    {
                        TrecsDebugAssert.That(
                            _poolManager.HasPool(entry.Type),
                            "Found non-null pointer value for type {0} with no associated memory pool",
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
            TrecsDebugAssert.That(!_isDisposed);

            TrecsDebugAssert.That(address != 0);

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
                throw TrecsDebugAssert.CreateException(
                    "Attempted to dispose invalid heap memory address ({0}) for type {1}",
                    address,
                    typeof(T)
                );
            }
        }

        internal void Serialize(ISerializationWriter writer)
        {
            writer.Write<uint>("IdCounter", _nextHandleId);
            writer.Write<int>("NumEntries", _entries.Count);

            foreach (var (address, entry) in _entries)
            {
                writer.PushScope("{0}_{1}", entry.Type.Name, address);
                writer.Write<uint>("Address", address);

                TrecsDebugAssert.That(entry.Type == entry.Value.GetType());
                writer.WriteObject("Obj", entry.Value);
                writer.PopScope();
            }
        }

        internal void Deserialize(ISerializationReader reader)
        {
            TrecsDebugAssert.That(!_isDisposed);
            // Defensive: callers contract is ClearAll() before Deserialize, but
            // a wrong-order call would silently corrupt state — warn-then-clean
            // so the contract violation is observable in dev builds while still
            // recoverable in release.
            ClearAll(warnUndisposed: true);

            _nextHandleId = reader.Read<uint>("IdCounter");

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
