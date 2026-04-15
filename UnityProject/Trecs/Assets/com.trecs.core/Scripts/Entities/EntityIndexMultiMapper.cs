using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Trecs.Collections;

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
        internal EntityIndexMultiMapper(DenseDictionary<Group, IComponentArray<T>> dictionary)
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
            ref var dict = ref _dic.GetValueByRef(entity.Group);
            return ref dict.GetValueAtIndexByRef(entity.Index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Exists(EntityIndex entity)
        {
            return _dic.TryGetIndex(entity.Group, out var index)
                && entity.Index < _dic.GetValueAtIndexByRef(index).Count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetEntity(EntityIndex entity, out T component)
        {
            component = default;
            if (_dic.TryGetIndex(entity.Group, out var index))
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
        public bool FindIndex(Group group, int entityIndex, out int index)
        {
            index = entityIndex;
            if (_dic.TryGetIndex(group, out var groupIndex))
            {
                return entityIndex < _dic.GetValueAtIndexByRef(groupIndex).Count;
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetIndex(Group group, int entityIndex)
        {
            return entityIndex;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetIndex(EntityIndex entityIndex)
        {
            return GetIndex(entityIndex.Group, entityIndex.Index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Exists(Group group, int entityIndex)
        {
            return _dic.TryGetIndex(group, out var groupIndex)
                && entityIndex < _dic.GetValueAtIndexByRef(groupIndex).Count;
        }

        public Type Template => TypeMeta<T>.Type;

        readonly DenseDictionary<Group, IComponentArray<T>> _dic;
    }
}
