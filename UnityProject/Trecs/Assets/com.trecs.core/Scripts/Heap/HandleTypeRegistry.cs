using System;
using System.Collections.Generic;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Managed-side (handle → <see cref="Type"/>) bookkeeping shared by the chunk-store-backed
    /// heaps (<see cref="NativeUniqueHeap"/>, <see cref="TrecsListHeap"/>, and
    /// <see cref="FrameScopedNativeUniqueHeap"/> for its type half). Encapsulates the standard
    /// add / remove / iterate / serialize loop so each heap only owns its heap-specific
    /// concerns. Uses <see cref="DenseDictionary{TKey,TValue}"/> for deterministic iteration
    /// order — required for snapshot byte determinism.
    /// </summary>
    internal sealed class HandleTypeRegistry
    {
        readonly DenseDictionary<uint, Type> _types = new();

        public int Count => _types.Count;

        /// <summary>
        /// Foreachable view of the handles. Backed by <see cref="DenseDictionary{TKey, TValue}.KeyEnumerable"/>
        /// so iteration is allocation-free and order is deterministic.
        /// </summary>
        public DenseDictionary<uint, Type>.KeyEnumerable Handles => _types.Keys;

        /// <summary>
        /// Foreachable view of (handle, type) pairs in deterministic order. Iterate as
        /// <c>foreach (var (handle, type) in registry.All)</c>.
        /// </summary>
        public DenseDictionary<uint, Type> All => _types;

        public bool ContainsKey(uint handle) => _types.ContainsKey(handle);

        public bool TryGetType(uint handle, out Type type) => _types.TryGetValue(handle, out type);

        public void Add(uint handle, Type type) => _types.Add(handle, type);

        public bool TryRemove(uint handle) => _types.TryRemove(handle);

        public void Clear() => _types.Clear();

        /// <summary>
        /// Writes the (handle → type) entries in deterministic iteration order. Format:
        /// <c>NumEntries</c>, then per entry <c>(Address: uint, InnerTypeId: int)</c>.
        /// </summary>
        public void Serialize(ISerializationWriter writer)
        {
            writer.Write<int>("NumEntries", _types.Count);
            foreach (var (address, type) in _types)
            {
                writer.Write<uint>("Address", address);
                writer.Write<int>("InnerTypeId", TypeIdProvider.GetTypeId(type));
            }
        }

        /// <summary>
        /// Reads the (handle → type) entries previously written by <see cref="Serialize"/>.
        /// Asserts the registry is empty on entry; caller's heap is responsible for
        /// clearing it (typically by calling <c>ClearAll</c> before <c>Deserialize</c>).
        /// </summary>
        public void Deserialize(ISerializationReader reader)
        {
            TrecsAssert.That(_types.Count == 0);
            var numEntries = reader.Read<int>("NumEntries");
            _types.EnsureCapacity(numEntries);
            for (int i = 0; i < numEntries; i++)
            {
                var address = reader.Read<uint>("Address");
                var innerTypeId = reader.Read<int>("InnerTypeId");
                _types.Add(address, TypeIdProvider.GetTypeFromId(innerTypeId));
            }
        }

        /// <summary>
        /// Pretty-prints the set of distinct type names currently registered. Used by
        /// heaps for undisposed-leak warnings.
        /// </summary>
        public string DescribeRegisteredTypes()
        {
            var names = new HashSet<string>();
            foreach (var (_, type) in _types)
            {
                names.Add(type.GetPrettyName());
            }
            return names.Join(", ");
        }
    }
}
