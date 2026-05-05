using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Trecs.Internal
{
    /// <summary>
    /// to retrieve an EntityIndexMultiMapper use entitiesQuerier.QueryMappedEntities<T>(groups);
    /// </summary>
    /// <typeparam name="T"></typeparam>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct EntityIndexMultiMapper<T>
        where T : unmanaged, IEntityComponent
    {
        internal EntityIndexMultiMapper(DenseDictionary<GroupIndex, IComponentArray<T>> dictionary)
        {
            _dic = dictionary;
        }

        public int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _dic.Count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T Entity(EntityIndex entity)
        {
#if DEBUG
            if (!Exists(entity))
                throw new TrecsException("EntityIndexMultiMapper: Entity not found");
#endif
            ref var dict = ref _dic.GetValueByRef(entity.GroupIndex);
            return ref dict.GetValueAtIndexByRef(entity.Index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Exists(EntityIndex entity)
        {
            return _dic.TryGetIndex(entity.GroupIndex, out var index)
                && entity.Index < _dic.GetValueAtIndexByRef(index).Count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetEntity(EntityIndex entity, out T component)
        {
            component = default;
            if (_dic.TryGetIndex(entity.GroupIndex, out var index))
            {
                ref var componentArray = ref _dic.GetValueAtIndexByRef(index);
                if (entity.Index < componentArray.Count)
                {
                    component = componentArray.GetValueAtIndexByRef(entity.Index);
                    return true;
                }
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool FindIndex(GroupIndex group, int entityIndex, out int index)
        {
            index = entityIndex;
            if (_dic.TryGetIndex(group, out var groupIndex))
            {
                return entityIndex < _dic.GetValueAtIndexByRef(groupIndex).Count;
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetIndex(GroupIndex group, int entityIndex)
        {
            return entityIndex;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetIndex(EntityIndex entityIndex)
        {
            return GetIndex(entityIndex.GroupIndex, entityIndex.Index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Exists(GroupIndex group, int entityIndex)
        {
            return _dic.TryGetIndex(group, out var groupIndex)
                && entityIndex < _dic.GetValueAtIndexByRef(groupIndex).Count;
        }

        public Type Template => TypeMeta<T>.Type;

        readonly DenseDictionary<GroupIndex, IComponentArray<T>> _dic;
    }
}
