using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Internal
{
    static class ComponentArrayUtilities
    {
        internal static EntityIndexMapper<T> ToEntityIndexMapper<T>(
            this IComponentArray<T> dic,
            GroupIndex groupStructId
        )
            where T : unmanaged, IEntityComponent
        {
            var mapper = new EntityIndexMapper<T>(groupStructId, dic);

            return mapper;
        }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class ComponentArray<TValue> : IComponentArray<TValue>
        where TValue : unmanaged, IEntityComponent
    {
        NativeList<TValue> _values;
        int _count;

        public ComponentArray(int size)
        {
            _values = new NativeList<TValue>(size, Allocator.Persistent);
            _values.Resize(size, NativeArrayOptions.ClearMemory);
            _count = 0;
        }

        public int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _count;
        }

        public Type ComponentType => typeof(TValue);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NativeBuffer<TValue> GetValues(out int count)
        {
            count = _count;
            return NativeBuffer<TValue>.FromNativeList(_values);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue GetValueAtIndexByRef(int index)
        {
            unsafe
            {
                return ref UnsafeUtility.ArrayElementAsRef<TValue>(_values.GetUnsafePtr(), index);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Add(in TValue entityComponent)
        {
            var index = _count;

            if (_count >= _values.Length)
            {
                _values.Resize((int)((_count + 1) * 1.5f), NativeArrayOptions.UninitializedMemory);
            }

            unsafe
            {
                UnsafeUtility.ArrayElementAsRef<TValue>(_values.GetUnsafePtr(), _count) =
                    entityComponent;
            }
            _count++;

            return index;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IComponentArray Create()
        {
            return new ComponentArray<TValue>(1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            _count = 0;
        }

        internal NativeList<TValue> RawValues => _values;

        public int ElementSize
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => UnsafeUtility.SizeOf<TValue>();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void* GetUnsafePtr()
        {
            return _values.GetUnsafePtr();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetCount(int count)
        {
            _count = count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureCapacity(int size)
        {
            if (size > _values.Length)
            {
                _values.Resize(size, NativeArrayOptions.UninitializedMemory);
            }
        }

        public void ResetToDefaultValuesWithCount(int count)
        {
            _values.Resize(count, NativeArrayOptions.UninitializedMemory);
            _count = count;
            if (count > 0)
            {
                unsafe
                {
                    UnsafeUtility.MemClear(
                        _values.GetUnsafePtr(),
                        UnsafeUtility.SizeOf<TValue>() * count
                    );
                }
            }
        }

        public void Dispose()
        {
            _values.Dispose();
            _count = 0;

            GC.SuppressFinalize(this);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddEntitiesToDictionary(IComponentArray toDictionary, GroupIndex groupId)
        {
            if (_count == 0)
            {
                return;
            }

            var toDic = (ComponentArray<TValue>)toDictionary;
            var newCount = toDic._count + _count;
            toDic.EnsureCapacity(newCount);

            unsafe
            {
                var elementSize = UnsafeUtility.SizeOf<TValue>();
                UnsafeUtility.MemCpy(
                    (byte*)toDic._values.GetUnsafePtr() + toDic._count * elementSize,
                    _values.GetUnsafeReadOnlyPtr(),
                    _count * elementSize
                );
            }

            toDic._count = newCount;
        }
    }
}
