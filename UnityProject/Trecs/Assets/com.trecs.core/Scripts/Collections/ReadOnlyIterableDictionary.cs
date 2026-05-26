using System;
using Trecs.Internal;

namespace Trecs.Collections
{
    // Does not implement IEnumerable by design: foreach over an interface-typed
    // variable boxes the struct enumerator, causing a GC allocation per iteration.
    public readonly struct ReadOnlyIterableDictionary<TKey, TValue>
        where TKey : struct, IEquatable<TKey>
    {
        private readonly IterableDictionary<TKey, TValue> _dictionary;

        public ReadOnlyIterableDictionary(IterableDictionary<TKey, TValue> dictionary)
        {
            TrecsDebugAssert.IsNotNull(dictionary);
            _dictionary = dictionary;
        }

        public static implicit operator ReadOnlyIterableDictionary<TKey, TValue>(
            IterableDictionary<TKey, TValue> value
        )
        {
            return new ReadOnlyIterableDictionary<TKey, TValue>(value);
        }

        public TValue this[TKey key] => _dictionary[key];

        public int Count => _dictionary.Count;

        public bool ContainsKey(TKey key) => _dictionary.ContainsKey(key);

        public bool IsEmpty => _dictionary.Count == 0;

        public bool TryGetValue(TKey key, out TValue result) =>
            _dictionary.TryGetValue(key, out result);

        public IterableDictionaryKeyEnumerable<TKey> Keys => _dictionary.Keys;

        public Enumerator GetEnumerator()
        {
            return new Enumerator(_dictionary.GetEnumerator());
        }

        public struct Enumerator
        {
            private IterableDictionary<TKey, TValue>.Enumerator _innerEnumerator;

            public Enumerator(IterableDictionary<TKey, TValue>.Enumerator enumerator)
            {
                _innerEnumerator = enumerator;
            }

            public bool MoveNext() => _innerEnumerator.MoveNext();

            public KvPair Current => new(_innerEnumerator.Current);
        }

        public readonly struct KvPair
        {
            private readonly IterableDictionary<TKey, TValue>.KvPair _inner;

            public KvPair(IterableDictionary<TKey, TValue>.KvPair kvp)
            {
                _inner = kvp;
            }

            public TKey Key => _inner.Key;
            public TValue Value => _inner.Value;

            public void Deconstruct(out TKey key, out TValue value)
            {
                key = Key;
                value = Value;
            }
        }
    }
}
