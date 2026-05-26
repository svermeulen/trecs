using System;
using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs.Collections
{
    // Implemented by IterableDictionary<TKey, TValue> so the TValue-free struct
    // enumerator can read the dictionary's mutation counter without taking
    // a typed reference (which would re-introduce TValue and break variance).
    internal interface IIterableDictionaryVersion<TKey>
        where TKey : struct, IEquatable<TKey>
    {
        ushort Version { get; }
    }

    public struct IterableDictionaryNode<TKey>
        where TKey : struct, IEquatable<TKey>
    {
        public int HashCode;
        public int Previous;
        public TKey Key;

        public IterableDictionaryNode(in TKey key, int hash, int previousNode)
        {
            Key = key;
            HashCode = hash;
            Previous = previousNode;
        }

        public IterableDictionaryNode(in TKey key, int hash)
        {
            Key = key;
            HashCode = hash;
            Previous = -1;
        }
    }

    public readonly struct IterableDictionaryKeyEnumerable<TKey>
        where TKey : struct, IEquatable<TKey>
    {
        readonly IterableDictionaryNode<TKey>[] _nodes;
        readonly int _count;
        readonly IIterableDictionaryVersion<TKey> _versionSource;

        internal IterableDictionaryKeyEnumerable(
            IterableDictionaryNode<TKey>[] nodes,
            int count,
            IIterableDictionaryVersion<TKey> versionSource
        )
        {
            _nodes = nodes;
            _count = count;
            _versionSource = versionSource;
        }

        public int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IterableDictionaryKeyEnumerator<TKey> GetEnumerator() =>
            new(_nodes, _count, _versionSource, _versionSource.Version);
    }

    public struct IterableDictionaryKeyEnumerator<TKey>
        where TKey : struct, IEquatable<TKey>
    {
        readonly IterableDictionaryNode<TKey>[] _nodes;
        readonly int _count;
        readonly IIterableDictionaryVersion<TKey> _versionSource;
        readonly ushort _expectedVersion;
        int _index;

        internal IterableDictionaryKeyEnumerator(
            IterableDictionaryNode<TKey>[] nodes,
            int count,
            IIterableDictionaryVersion<TKey> versionSource,
            ushort expectedVersion
        )
        {
            _nodes = nodes;
            _count = count;
            _versionSource = versionSource;
            _expectedVersion = expectedVersion;
            _index = -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            TrecsDebugAssert.That(
                _versionSource.Version == _expectedVersion,
                "IterableDictionary modified during key iteration"
            );
            return ++_index < _count;
        }

        public TKey Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _nodes[_index].Key;
        }
    }

    // Exists to provide covariance over TValue, which the readonly-struct
    // ReadOnlyIterableDictionary<TKey, TValue> cannot — C# variance is only
    // allowed on interface and delegate type parameters. Lets owners store
    // IterableDictionary<TKey, Concrete> and expose IReadOnlyIterableDictionary<TKey, IBase>
    // without copying.
    //
    // Notable omissions vs the BCL IReadOnlyDictionary shape, both forced by
    // variance rules:
    //   - TryGetValue(TKey, out TValue) — out TValue is rejected as a covariant
    //     position by Unity's Roslyn (CS1961). Use ContainsKey + indexer if you
    //     only have the interface, or hold the concrete IterableDictionary / struct
    //     ReadOnlyIterableDictionary view (both have TryGetValue).
    //   - Values and full pair enumeration — would put TValue inside a struct
    //     return type, which forces TValue invariant. Use Keys + indexer, or
    //     drop down to the concrete dictionary for pair enumeration.
    public interface IReadOnlyIterableDictionary<TKey, out TValue>
        where TKey : struct, IEquatable<TKey>
    {
        TValue this[TKey key] { get; }
        int Count { get; }
        bool IsEmpty { get; }
        bool ContainsKey(TKey key);
        IterableDictionaryKeyEnumerable<TKey> Keys { get; }
    }
}
