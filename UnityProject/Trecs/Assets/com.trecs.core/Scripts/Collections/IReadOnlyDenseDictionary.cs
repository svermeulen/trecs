using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs.Collections
{
    // Implemented by DenseDictionary<TKey, TValue> so the TValue-free struct
    // enumerator can read the dictionary's mutation counter without taking
    // a typed reference (which would re-introduce TValue and break variance).
    internal interface IDenseDictionaryVersion<TKey>
    {
        ushort Version { get; }
    }

    public struct DenseDictionaryNode<TKey>
    {
        public int HashCode;
        public int Previous;
        public TKey Key;

        public DenseDictionaryNode(in TKey key, int hash, int previousNode)
        {
            Key = key;
            HashCode = hash;
            Previous = previousNode;
        }

        public DenseDictionaryNode(in TKey key, int hash)
        {
            Key = key;
            HashCode = hash;
            Previous = -1;
        }
    }

    public readonly struct DenseDictionaryKeyEnumerable<TKey>
    {
        readonly DenseDictionaryNode<TKey>[] _nodes;
        readonly int _count;
        readonly IDenseDictionaryVersion<TKey> _versionSource;

        internal DenseDictionaryKeyEnumerable(
            DenseDictionaryNode<TKey>[] nodes,
            int count,
            IDenseDictionaryVersion<TKey> versionSource
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
        public DenseDictionaryKeyEnumerator<TKey> GetEnumerator() =>
            new(_nodes, _count, _versionSource, _versionSource.Version);
    }

    public struct DenseDictionaryKeyEnumerator<TKey>
    {
        readonly DenseDictionaryNode<TKey>[] _nodes;
        readonly int _count;
        readonly IDenseDictionaryVersion<TKey> _versionSource;
        readonly ushort _expectedVersion;
        int _index;

        internal DenseDictionaryKeyEnumerator(
            DenseDictionaryNode<TKey>[] nodes,
            int count,
            IDenseDictionaryVersion<TKey> versionSource,
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
                "DenseDictionary modified during key iteration"
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
    // ReadOnlyDenseDictionary<TKey, TValue> cannot — C# variance is only
    // allowed on interface and delegate type parameters. Lets owners store
    // DenseDictionary<TKey, Concrete> and expose IReadOnlyDenseDictionary<TKey, IBase>
    // without copying.
    //
    // Notable omissions vs the BCL IReadOnlyDictionary shape, both forced by
    // variance rules:
    //   - TryGetValue(TKey, out TValue) — out TValue is rejected as a covariant
    //     position by Unity's Roslyn (CS1961). Use ContainsKey + indexer if you
    //     only have the interface, or hold the concrete DenseDictionary / struct
    //     ReadOnlyDenseDictionary view (both have TryGetValue).
    //   - Values and full pair enumeration — would put TValue inside a struct
    //     return type, which forces TValue invariant. Use Keys + indexer, or
    //     drop down to the concrete dictionary for pair enumeration.
    public interface IReadOnlyDenseDictionary<TKey, out TValue>
    {
        TValue this[TKey key] { get; }
        int Count { get; }
        bool IsEmpty { get; }
        bool ContainsKey(TKey key);
        DenseDictionaryKeyEnumerable<TKey> Keys { get; }
    }
}
