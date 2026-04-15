using System.Collections;
using System.Collections.Generic;
using Trecs.Internal;

namespace Trecs.Collections
{
    ///   NOTE: accessing via IEnumerable is less performant and may contain boxing
    public readonly struct ReadOnlyDenseDictionary<TKey, TValue>
        : IEnumerable<DenseDictionary<TKey, TValue>.KvPair>
    {
        private readonly DenseDictionary<TKey, TValue> _dictionary;

        public ReadOnlyDenseDictionary(DenseDictionary<TKey, TValue> dictionary)
        {
            Assert.IsNotNull(dictionary);
            _dictionary = dictionary;
        }

        public static implicit operator ReadOnlyDenseDictionary<TKey, TValue>(
            DenseDictionary<TKey, TValue> value
        )
        {
            return new ReadOnlyDenseDictionary<TKey, TValue>(value);
        }

        public TValue this[TKey key] => _dictionary[key];

        public int Count => _dictionary.Count;

        public bool ContainsKey(TKey key) => _dictionary.ContainsKey(key);

        public bool IsEmpty()
        {
            return _dictionary.Count == 0;
        }

        public bool TryGetValue(TKey key, out TValue result) =>
            _dictionary.TryGetValue(key, out result);

        public Enumerator GetEnumerator()
        {
            return new Enumerator(_dictionary.GetEnumerator());
        }

        IEnumerator<DenseDictionary<TKey, TValue>.KvPair> IEnumerable<DenseDictionary<
            TKey,
            TValue
        >.KvPair>.GetEnumerator()
        {
            return ((IEnumerable<DenseDictionary<TKey, TValue>.KvPair>)_dictionary).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return ((IEnumerable<DenseDictionary<TKey, TValue>.KvPair>)_dictionary).GetEnumerator();
        }

        public struct Enumerator
        {
            private DenseDictionary<TKey, TValue>.Enumerator _innerEnumerator;

            public Enumerator(DenseDictionary<TKey, TValue>.Enumerator enumerator)
            {
                _innerEnumerator = enumerator;
            }

            public bool MoveNext() => _innerEnumerator.MoveNext();

            public KvPair Current => new(_innerEnumerator.Current);
        }

        public readonly struct KvPair
        {
            private readonly DenseDictionary<TKey, TValue>.KvPair _inner;

            public KvPair(DenseDictionary<TKey, TValue>.KvPair kvp)
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
