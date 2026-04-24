using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Trecs.Internal
{
    /// <summary>
    /// </summary>
    /// <typeparam name="T"></typeparam>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct EntityIndexMapper<T> : IEntityIndexMapper
        where T : unmanaged, IEntityComponent
    {
        public int Count => _map.Count;
        public GroupIndex GroupId { get; }
        public Type Template => TypeMeta<T>.Type;

        internal EntityIndexMapper(GroupIndex groupStructId, IComponentArray<T> dic)
            : this()
        {
            GroupId = groupStructId;
            _map = dic;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T Entity(int index)
        {
#if DEBUG
            if (_map == null)
                throw new TrecsException(
                    $"Not initialized EntityIndexMapper in this group {typeof(T)}"
                );
            if (index >= _map.Count)
                throw new TrecsException($"Entity not found in this group {typeof(T)}");
#endif
            return ref _map.GetValueAtIndexByRef(index);
        }

        public bool TryGetEntity(int index, out T value)
        {
            if (_map != null && index < _map.Count)
            {
                value = _map.GetValueAtIndexByRef(index);
                return true;
            }

            value = default;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Exists(int index)
        {
            return _map.Count > 0 && index < _map.Count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetIndex(int index)
        {
            return index;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool FindIndex(int valueKey, out int index)
        {
            index = valueKey;
            return valueKey < _map.Count;
        }

        internal readonly IComponentArray<T> _map;
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface IEntityIndexMapper
    {
        bool FindIndex(int valueKey, out int index);
        int GetIndex(int index);
        bool Exists(int index);

        GroupIndex GroupId { get; }
        Type Template { get; }
    }
}
