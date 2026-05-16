using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs.Collections
{
    /// <summary>
    /// This dictionary has been created for the following reasons:
    ///
    /// 1. We needed a dictionary that would let us iterate over the values as an array, directly,
    /// without generating one or using an iterator. For this goal is N times faster than the standard
    /// dictionary. Should be similar performance to the standard dictionary for most of the operations.
    /// The only slower operation is resizing the memory on add, as this implementation needs to use
    /// two separate arrays compared to the standard one.
    ///
    /// 2. We needed it to be deterministic
    ///
    /// Supports any key type that implements IEquatable<T>, including strings, enums, and custom types.
    /// </summary>
    public sealed class DenseDictionary<TKey, TValue>
        : IEnumerable<DenseDictionary<TKey, TValue>.KvPair>
    {
        Node[] _valuesInfo;
        TValue[] _values;
        int[] _buckets;

        int _freeValueCellIndex;
        int _collisions;
        ulong _fastModBucketsMultiplier;

        public DenseDictionary(int size)
        {
            TrecsAssert.That(size >= 0, "DenseDictionary size must be non-negative");

            _valuesInfo = new Node[size];
            _values = new TValue[size];
            _buckets = new int[HashHelpers.GetPrime(size)];

            if (size > 0)
            {
                _fastModBucketsMultiplier = HashHelpers.GetFastModMultiplier((uint)_buckets.Length);
            }
        }

        public DenseDictionary()
            : this(1) { }

        static DenseDictionary()
        {
#if DEBUG
            try
            {
                var keyType = typeof(TKey);
                if (
                    keyType.IsValueType
                    && !keyType.IsEnum
                    && keyType.GetMethod(
                        "GetHashCode",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly
                    ) == null
                )
                    TrecsLog.Default.Warning(
                        "{0} does not implement GetHashCode -> This will cause unwanted allocations (boxing)",
                        keyType.Name
                    );
            }
            catch (AmbiguousMatchException) { }
#endif
        }

        public TValue this[TKey key]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _values[GetIndex(key)];
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                AddValue(key, out var index);

                _values[index] = value;
            }
        }

        public Node[] UnsafeKeys
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _valuesInfo;
        }

        public TValue[] UnsafeValues
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _values;
        }

        public bool IsEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Count == 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TValue[] UnsafeGetValues(out int count)
        {
            count = _freeValueCellIndex;

            return _values;
        }

        public int Count => _freeValueCellIndex;

        public KeyEnumerable Keys => new KeyEnumerable(this);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        //note, this returns readonly because the enumerator cannot be, but at the same time, it cannot be modified
        public Enumerator GetEnumerator()
        {
            return new Enumerator(this);
        }

        IEnumerator<KvPair> IEnumerable<KvPair>.GetEnumerator()
        {
            return GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(TKey key, in TValue value)
        {
            var itemAdded = AddValue(key, out var index);
            if (!itemAdded)
            {
                throw TrecsAssert.CreateException(
                    "Key {0} already present in DenseDictionary<{1}, {2}>",
                    key,
                    typeof(TKey),
                    typeof(TValue)
                );
            }
            _values[index] = value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryAdd(TKey key, in TValue value, out int index)
        {
            var itemAdded = AddValue(key, out index);

            if (itemAdded)
            {
                _values[index] = value;
            }

            return itemAdded;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(TKey key, in TValue value)
        {
            var itemAdded = AddValue(key, out var index);
            TrecsAssert.That(!itemAdded, "trying to set a value on a not existing key");
            _values[index] = value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Recycle()
        {
            // Clear only the buckets that were actually written to, using the
            // stored hash codes. This is O(entries) instead of O(bucket capacity),
            // which is much faster when the dictionary is large but sparsely used.
            var bucketsCapacity = (uint)_buckets.Length;
            for (int i = 0; i < _freeValueCellIndex; i++)
            {
                var bucketIndex = Reduce(
                    (uint)_valuesInfo[i].hashcode,
                    bucketsCapacity,
                    _fastModBucketsMultiplier
                );
                _buckets[bucketIndex] = 0;
            }

            _freeValueCellIndex = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            if (_freeValueCellIndex == 0)
            {
                return;
            }

            _freeValueCellIndex = 0;

            Array.Clear(_buckets, 0, _buckets.Length);
            Array.Clear(_values, 0, _values.Length);
            Array.Clear(_valuesInfo, 0, _valuesInfo.Length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        //WARNING this method must stay stateless (not relying on states that can change, it's ok to read
        //constant states) because it will be used in multithreaded parallel code
        public bool ContainsKey(TKey key)
        {
            return TryGetIndex(key, out _);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        //WARNING this method must stay stateless (not relying on states that can change, it's ok to read
        //constant states) because it will be used in multithreaded parallel code
        public bool TryGetValue(TKey key, out TValue result)
        {
            if (TryGetIndex(key, out var findIndex) == true)
            {
                result = _values[findIndex];
                return true;
            }

            result = default;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue GetOrAdd(TKey key)
        {
            if (TryGetIndex(key, out var findIndex) == true)
            {
                return ref _values[findIndex];
            }

            AddValue(key, out findIndex);

            _values[findIndex] = default;

            return ref _values[findIndex];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue GetOrAdd(TKey key, Func<TValue> builder)
        {
            if (TryGetIndex(key, out var findIndex) == true)
            {
                return ref _values[findIndex];
            }

            AddValue(key, out findIndex);

            _values[findIndex] = builder();

            return ref _values[findIndex];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue GetOrAdd(TKey key, out int index)
        {
            if (TryGetIndex(key, out index) == true)
            {
                return ref _values[index];
            }

            AddValue(key, out index);

            return ref _values[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue GetOrAdd<W>(TKey key, FuncRef<W, TValue> builder, ref W parameter)
        {
            if (TryGetIndex(key, out var findIndex) == true)
            {
                return ref _values[findIndex];
            }

            AddValue(key, out findIndex);

            _values[findIndex] = builder(ref parameter);

            return ref _values[findIndex];
        }

        /// <summary>
        /// This must be unit tested properly
        /// </summary>
        /// <param name="key"></param>
        /// <param name="builder"></param>
        /// <param name="recycler"></param>
        /// <typeparam name="TValueProxy"></typeparam>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue RecycleOrAdd<TValueProxy>(
            TKey key,
            Func<TValueProxy> builder,
            ActionRef<TValueProxy> recycler
        )
            where TValueProxy : class, TValue
        {
            if (TryGetIndex(key, out var findIndex))
            {
                return ref _values[findIndex];
            }

            AddValue(key, out findIndex);

            if (_values[findIndex] == null)
            {
                _values[findIndex] = builder();
            }
            else
            {
                recycler(ref Unsafe.As<TValue, TValueProxy>(ref _values[findIndex]));
            }

            return ref _values[findIndex];
        }

        /// <summary>
        /// RecycledOrCreate makes sense to use on dictionaries that are fast cleared and use objects
        /// as value. Once the dictionary is fast cleared, it will try to reuse object values that are
        /// recycled during the fast clearing.
        /// </summary>
        /// <param name="key"></param>
        /// <param name="builder"></param>
        /// <param name="recycler"></param>
        /// <param name="parameter"></param>
        /// <typeparam name="TValueProxy"></typeparam>
        /// <typeparam name="W"></typeparam>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue RecycleOrAdd<TValueProxy, W>(
            TKey key,
            FuncRef<W, TValue> builder,
            ActionRef<TValueProxy, W> recycler,
            ref W parameter
        )
            where TValueProxy : class, TValue
        {
            if (TryGetIndex(key, out var findIndex) == true)
            {
                return ref _values[findIndex];
            }

            AddValue(key, out findIndex);

            if (_values[findIndex] == null)
            {
                _values[findIndex] = builder(ref parameter);
            }
            else
            {
                recycler(ref Unsafe.As<TValue, TValueProxy>(ref _values[findIndex]), ref parameter);
            }

            return ref _values[findIndex];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        //WARNING this method must stay stateless (not relying on states that can change, it's ok to read
        //constant states) because it will be used in multi-threaded parallel code
        public ref TValue GetValueAtIndexByRef(int index)
        {
            return ref _values[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue GetValueByRef(TKey key)
        {
#if DEBUG
            if (TryGetIndex(key, out var findIndex))
                return ref _values[findIndex];

            throw TrecsAssert.CreateException("Key not found");
#else
            //Burst is not able to vectorise code if throw is found, regardless if it's actually ever thrown
            TryGetIndex(key, out var findIndex);

            return ref _values[findIndex];
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureCapacity(int size)
        {
            if (_values.Length < size)
            {
                var expandPrime = HashHelpers.Expand(size);

                Array.Resize(ref _values, expandPrime);
                Array.Resize(ref _valuesInfo, expandPrime);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void IncreaseCapacityBy(int size)
        {
            var expandPrime = HashHelpers.Expand(_values.Length + size);

            Array.Resize(ref _values, expandPrime);
            Array.Resize(ref _valuesInfo, expandPrime);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryRemove(TKey key)
        {
            return TryRemove(key, out _, out _);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryRemove(TKey key, out TValue val)
        {
            return TryRemove(key, out _, out val);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RemoveMustExist(in TKey key)
        {
            var wasRemoved = TryRemove(key);
            TrecsAssert.That(wasRemoved);
        }

        public TValue RemoveAndGet(in TKey key)
        {
            if (TryRemove(key, out _, out var value))
            {
                return value;
            }

            throw TrecsAssert.CreateException("Dictionary key {0} not found", key);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryRemove(TKey key, out int index, out TValue value)
        {
            int hash = key.GetHashCode();
            int bucketIndex = Reduce((uint)hash, (uint)_buckets.Length, _fastModBucketsMultiplier);

            //find the bucket
            int indexToValueToRemove = _buckets[bucketIndex] - 1;
            int itemAfterCurrentOne = -1;

            //Part one: look for the actual key in the bucket list if found I update the bucket list so that it doesn't
            //point anymore to the cell to remove
            while (indexToValueToRemove != -1)
            {
                ref var dictionaryNode = ref _valuesInfo[indexToValueToRemove];
                if (dictionaryNode.hashcode == hash && _keyComp.Equals(dictionaryNode.key, key))
                {
                    //if the key is found and the bucket points directly to the node to remove
                    if (_buckets[bucketIndex] - 1 == indexToValueToRemove)
                    {
                        //the bucket will point to the previous cell. if a previous cell exists
                        //its next pointer must be updated!
                        //<--- iteration order
                        //                      Bucket points always to the last one
                        //   ------- ------- -------
                        //   |  1  | |  2  | |  3  | //bucket cannot have next, only previous
                        //   ------- ------- -------
                        //--> insert order
                        _buckets[bucketIndex] = dictionaryNode.previous + 1;
                    }
                    else //we need to update the previous pointer if it's not the last element that is removed
                    {
                        TrecsAssert.That(itemAfterCurrentOne != -1, "this should never happen");
                        //update the previous pointer of the item after the one to remove with the previous pointer of the item to remove
                        _valuesInfo[itemAfterCurrentOne].previous = dictionaryNode.previous;
                    }

                    break; //don't miss this, at this point it must break and not update indexToValueToRemove
                }

                //a bucket always points to the last element of the list, so if the item is not found we need to iterate backward
                itemAfterCurrentOne = indexToValueToRemove;
                indexToValueToRemove = dictionaryNode.previous;
            }

            if (indexToValueToRemove == -1)
            {
                index = default;
                value = default;
                return false; //not found!
            }

            index = indexToValueToRemove; //index is a out variable, for internal use we want to know the index of the element to remove

            _freeValueCellIndex--; //one less value to iterate
            value = _values[indexToValueToRemove]; //value is a out variable, we want to know the value of the element to remove

            //Part two:
            //At this point nodes pointers and buckets are updated, but the _values array
            //still has got the value to delete. Remember the goal of this dictionary is to be able
            //to iterate over the values like an array, so the values array must always be up to date

            //if the cell to remove is the last one in the list, we can perform less operations (no swapping needed)
            //otherwise we want to move the last value cell over the value to remove

            var lastValueCellIndex = _freeValueCellIndex;
            if (indexToValueToRemove != lastValueCellIndex)
            {
                //we can transfer the last value of both arrays to the index of the value to remove.
                //in order to do so, we need to be sure that the bucket pointer is updated.
                //first we find the index in the bucket list of the pointer that points to the cell
                //to move
                ref var dictionaryNodeToMove = ref _valuesInfo[lastValueCellIndex];

                var movingBucketIndex = Reduce(
                    (uint)dictionaryNodeToMove.hashcode,
                    (uint)_buckets.Length,
                    _fastModBucketsMultiplier
                );

                var linkedListIterationIndex = _buckets[movingBucketIndex] - 1;

                //if the key is found and the bucket points directly to the node to remove
                //it must now point to the cell where it's going to be moved (update bucket list first linked list node to iterate from)
                if (linkedListIterationIndex == lastValueCellIndex)
                {
                    _buckets[movingBucketIndex] = indexToValueToRemove + 1;
                }

                //find the prev element of the last element in the valuesInfo array
                while (
                    _valuesInfo[linkedListIterationIndex].previous != -1
                    && _valuesInfo[linkedListIterationIndex].previous != lastValueCellIndex
                )
                {
                    linkedListIterationIndex = _valuesInfo[linkedListIterationIndex].previous;
                }

                //if we find any value that has the last value cell as previous, we need to update it to point to the new value index that is going to be replaced
                if (_valuesInfo[linkedListIterationIndex].previous != -1)
                {
                    _valuesInfo[linkedListIterationIndex].previous = indexToValueToRemove;
                }

                //finally, actually move the values
                _valuesInfo[indexToValueToRemove] = dictionaryNodeToMove;
                _values[indexToValueToRemove] = _values[lastValueCellIndex];
            }

            // Clear the now-unused slot to allow GC to collect the value
            _values[_freeValueCellIndex] = default(TValue);
            _valuesInfo[_freeValueCellIndex] = default(Node);

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Trim()
        {
            Array.Resize(ref _values, _freeValueCellIndex);
            Array.Resize(ref _valuesInfo, _freeValueCellIndex);
        }

        //I store all the index with an offset + 1, so that in the bucket list 0 means actually not existing.
        //When read the offset must be offset by -1 again to be the real one. In this way
        //I avoid to initialize the array to -1

        //WARNING this method must stay stateless (not relying on states that can change, it's ok to read
        //constant states) because it will be used in multithreaded parallel code
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetIndex(TKey key, out int findIndex)
        {
            TrecsAssert.That(
                _buckets.Length > 0,
                "Dictionary arrays are not correctly initialized (0 size)"
            );

            int hash = key.GetHashCode();

            int bucketIndex = Reduce((uint)hash, (uint)_buckets.Length, _fastModBucketsMultiplier);

            int valueIndex = _buckets[bucketIndex] - 1;

            //even if we found an existing value we need to be sure it's the one we requested
            while (valueIndex != -1)
            {
                ref var dictionaryNode = ref _valuesInfo[valueIndex];
                if (dictionaryNode.hashcode == hash && _keyComp.Equals(dictionaryNode.key, key))
                {
                    //this is the one
                    findIndex = valueIndex;
                    return true;
                }

                valueIndex = dictionaryNode.previous;
            }

            findIndex = 0;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetIndex(TKey key)
        {
#if DEBUG
            if (TryGetIndex(key, out var findIndex) == true)
                return findIndex;

            throw TrecsAssert.CreateException("Key {0} not found in DenseDictionary", key);
#else
            //Burst is not able to vectorise code if throw is found, regardless if it's actually ever thrown
            TryGetIndex(key, out var findIndex);

            return findIndex;
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Intersect<OTValue>(DenseDictionary<TKey, OTValue> otherDicKeys)
        {
            for (int i = Count - 1; i >= 0; i--)
            {
                var tKey = UnsafeKeys[i].key;
                if (!otherDicKeys.ContainsKey(tKey))
                {
                    TryRemove(tKey);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Exclude<OTValue>(DenseDictionary<TKey, OTValue> otherDicKeys)
        {
            for (int i = Count - 1; i >= 0; i--)
            {
                var tKey = UnsafeKeys[i].key;
                if (otherDicKeys.ContainsKey(tKey) == true)
                    TryRemove(tKey);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Union(DenseDictionary<TKey, TValue> otherDicKeys)
        {
            foreach (var other in otherDicKeys)
            {
                this[other.Key] = other.Value;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool AddValue(TKey key, out int indexSet)
        {
            int hash = key.GetHashCode(); //IEquatable doesn't enforce the override of GetHashCode
            int bucketIndex = Reduce((uint)hash, (uint)_buckets.Length, _fastModBucketsMultiplier);

            //buckets value -1 means it's empty
            var valueIndex = _buckets[bucketIndex] - 1;

            if (valueIndex == -1)
            {
                ResizeIfNeeded();
                //create the info node at the last position and fill it with the relevant information
                _valuesInfo[_freeValueCellIndex] = new Node(key, hash);
            }
            else //collision or already exists
            {
                int currentValueIndex = valueIndex;
                do
                {
                    ref var dictionaryNode = ref _valuesInfo[currentValueIndex];
                    if (dictionaryNode.hashcode == hash && _keyComp.Equals(dictionaryNode.key, key))
                    {
                        //the key already exists, simply replace the value!
                        indexSet = currentValueIndex;
                        return false;
                    }

                    currentValueIndex = dictionaryNode.previous;
                } while (currentValueIndex != -1); //-1 means no more values with key with the same hash

                ResizeIfNeeded();

                //oops collision!
                _collisions++;
                //create a new node which previous index points to node currently pointed in the bucket (valueIndex)
                //_freeValueCellIndex = valueIndex + 1
                _valuesInfo[_freeValueCellIndex] = new Node(key, hash, valueIndex);
                //Important: the new node is always the one that will be pointed by the bucket cell
                //so I can assume that the one pointed by the bucket is always the last value added
            }

            //item with this bucketIndex will point to the last value created
            //ToDo: if instead I assume that the original one is the one in the bucket
            //I wouldn't need to update the bucket here. Small optimization but important
            _buckets[bucketIndex] = _freeValueCellIndex + 1;

            indexSet = _freeValueCellIndex;
            _freeValueCellIndex++;

            //too many collisions
            if (_collisions > _buckets.Length)
            {
                if (_buckets.Length < 100)
                {
                    RecomputeBuckets(_collisions << 1);
                }
                else
                {
                    RecomputeBuckets(HashHelpers.Expand(_collisions));
                }
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void RecomputeBuckets(int newSize)
        {
            //we need more space and less collisions
            _buckets = new int[newSize];
            _collisions = 0;
            _fastModBucketsMultiplier = HashHelpers.GetFastModMultiplier((uint)_buckets.Length);
            var bucketsCapacity = (uint)_buckets.Length;

            //we need to get all the hash code of all the values stored so far and spread them over the new bucket
            //length
            var freeValueCellIndex = _freeValueCellIndex;
            for (int newValueIndex = 0; newValueIndex < freeValueCellIndex; ++newValueIndex)
            {
                //get the original hash code and find the new bucketIndex due to the new length
                ref var valueInfoNode = ref _valuesInfo[newValueIndex];
                var bucketIndex = Reduce(
                    (uint)valueInfoNode.hashcode,
                    bucketsCapacity,
                    _fastModBucketsMultiplier
                );
                //bucketsIndex can be -1 or a next value. If it's -1 means no collisions. If there is collision,
                //we create a new node which prev points to the old one. Old one next points to the new one.
                //the bucket will now points to the new one
                //In this way we can rebuild the linkedlist.
                //get the current valueIndex, it's -1 if no collision happens
                int existingValueIndex = _buckets[bucketIndex] - 1;
                //update the bucket index to the index of the current item that share the bucketIndex
                //(last found is always the one in the bucket)
                _buckets[bucketIndex] = newValueIndex + 1;
                if (existingValueIndex == -1)
                {
                    //ok nothing was indexed, the bucket was empty. We need to update the previous
                    //values of next and previous
                    valueInfoNode.previous = -1;
                }
                else
                {
                    //oops a value was already being pointed by this cell in the new bucket list,
                    //it means there is a collision, problem
                    _collisions++;
                    //the bucket will point to this value, so
                    //the previous index will be used as previous for the new value.
                    valueInfoNode.previous = existingValueIndex;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void ResizeIfNeeded()
        {
            if (_freeValueCellIndex == _values.Length)
            {
                var expandPrime = HashHelpers.Expand(_freeValueCellIndex);

                Array.Resize(ref _values, expandPrime);
                Array.Resize(ref _valuesInfo, expandPrime);
            }
        }

        static readonly bool Is64BitProcess = Environment.Is64BitProcess;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Reduce(uint hashcode, uint N, ulong fastModBucketsMultiplier)
        {
            if (hashcode >= N) //is the condition return actually an optimization?
                return (int)(
                    Is64BitProcess
                        ? HashHelpers.FastMod(hashcode, N, fastModBucketsMultiplier)
                        : hashcode % N
                );

            return (int)hashcode;
        }

        public readonly struct KeyEnumerable
        {
            readonly DenseDictionary<TKey, TValue> _dic;

            public KeyEnumerable(DenseDictionary<TKey, TValue> dic)
            {
                _dic = dic;
            }

            public int Count
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => _dic.Count;
            }

            public KeyEnumerator GetEnumerator() => new(_dic);
        }

        public struct KeyEnumerator
        {
            public KeyEnumerator(DenseDictionary<TKey, TValue> dic)
                : this()
            {
                _dic = dic;
                _index = -1;
                _count = dic.Count;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool MoveNext()
            {
                TrecsAssert.That(
                    _count == _dic.Count,
                    "can't modify a dictionary during its iteration"
                );

                if (_index < _count - 1)
                {
                    ++_index;
                    return true;
                }

                return false;
            }

            public readonly TKey Current => _dic._valuesInfo[_index].key;

            readonly DenseDictionary<TKey, TValue> _dic;
            readonly int _count;

            int _index;
        }

        /// <summary>
        ///the mechanism to use arrays is fundamental to work
        /// </summary>
        public readonly struct KvPair
        {
            public KvPair(in TKey key, TValue[] dicValues, int index)
            {
                _dicValues = dicValues;
                _index = index;
                _key = key;
            }

            public void Deconstruct(out TKey key, out TValue value)
            {
                key = this.Key;
                value = this.Value;
            }

            public TKey Key => _key;
            public ref TValue Value => ref _dicValues[_index];

            readonly TValue[] _dicValues;
            readonly TKey _key;
            readonly int _index;
        }

        public struct Enumerator : IEnumerator<KvPair>, IEnumerator
        {
            DenseDictionary<TKey, TValue> _dic;
#if DEBUG
            int _startCount;
#endif
            int _count;

            int _index;

            public Enumerator(in DenseDictionary<TKey, TValue> dic)
                : this()
            {
                _dic = dic;
                _index = -1;
                _count = dic.Count;
#if DEBUG
                _startCount = dic.Count;
#endif
            }

            public KvPair Current => new(_dic._valuesInfo[_index].key, _dic._values, _index);

            object IEnumerator.Current => Current;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool MoveNext()
            {
#if DEBUG
                TrecsAssert.That(
                    _count == _startCount,
                    "can't modify a dictionary while it is iterated"
                );
#endif
                if (_index < _count - 1)
                {
                    ++_index;
                    return true;
                }

                return false;
            }

            public void Reset()
            {
                _index = -1;
            }

            public void Dispose()
            {
                // Nothing to dispose for a struct enumerator
                // This is here to satisfy the IEnumerator<T> interface
            }

            public void SetRange(int startIndex, int count)
            {
                _index = startIndex - 1;
                _count = count;
#if DEBUG
                TrecsAssert.That(
                    _count <= _startCount,
                    "can't set a count greater than the starting one"
                );
                _startCount = count;
#endif
            }
        }

        public struct Node
        {
            public int hashcode;
            public int previous;
            public TKey key;

            public Node(in TKey key, int hash, int previousNode)
            {
                this.key = key;
                hashcode = hash;
                previous = previousNode;
            }

            public Node(in TKey key, int hash)
            {
                this.key = key;
                hashcode = hash;
                previous = -1;
            }
        }

        static readonly EqualityComparer<TKey> _keyComp = EqualityComparer<TKey>.Default;

        /// <summary>
        /// Gets the internal count of elements for serialization.
        /// </summary>
        public ref int UnsafeFreeValueCellIndex => ref _freeValueCellIndex;

        /// <summary>
        /// Gets the internal buckets array for serialization.
        /// </summary>
        public int[] UnsafeBuckets => _buckets;

        /// <summary>
        /// Gets the buckets capacity for serialization.
        /// </summary>
        public int UnsafeBucketsCapacity => _buckets.Length;

        /// <summary>
        /// Gets the internal collision counter for serialization.
        /// </summary>
        public ref int UnsafeCollisions => ref _collisions;

        /// <summary>
        /// Gets the internal fast mod multiplier for serialization.
        /// </summary>
        public ref ulong UnsafeFastModBucketsMultiplier => ref _fastModBucketsMultiplier;

        /// <summary>
        /// Ensures internal arrays have at least the specified capacity for deserialization.
        /// </summary>
        public void UnsafeEnsureCapacityForDeserialization(int valuesCount, int bucketsCapacity)
        {
            if (valuesCount > _valuesInfo.Length)
            {
                var newCapacity = HashHelpers.Expand(valuesCount);
                _valuesInfo = new Node[newCapacity];
                _values = new TValue[newCapacity];
            }

            // ALWAYS resize buckets to exact capacity (not just when larger)
            // This ensures _buckets.Length matches _fastModBucketsMultiplier
            if (bucketsCapacity != _buckets.Length)
            {
                _buckets = new int[bucketsCapacity];
            }
        }
    }
}
