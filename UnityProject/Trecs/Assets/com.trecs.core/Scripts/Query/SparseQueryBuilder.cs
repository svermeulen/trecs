using Trecs.Collections;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// A query builder that has a set applied. Reached by calling
    /// <see cref="QueryBuilder.InSet{T}"/> on a <see cref="QueryBuilder"/>.
    /// Entities must belong to the set to be matched.
    /// </summary>
    public ref struct SparseQueryBuilder
    {
        readonly WorldAccessor _world;

        TagSet _positiveTags;
        TagSet _negativeTags;
        ComponentTypeIdSet _positiveComps;
        ComponentTypeIdSet _negativeComps;
        readonly SetId _set;

        internal SparseQueryBuilder(
            WorldAccessor world,
            TagSet positiveTags,
            TagSet negativeTags,
            ComponentTypeIdSet positiveComps,
            ComponentTypeIdSet negativeComps,
            SetId setId
        )
        {
            _world = world;
            _positiveTags = positiveTags;
            _negativeTags = negativeTags;
            _positiveComps = positiveComps;
            _negativeComps = negativeComps;
            _set = setId;
        }

        public readonly WorldAccessor World => _world;

        public SparseQueryBuilder WithTags<T1>()
            where T1 : struct, ITag
        {
            _positiveTags = MergeTags(_positiveTags, TagSet<T1>.Value);
            return this;
        }

        public SparseQueryBuilder WithTags<T1, T2>()
            where T1 : struct, ITag
            where T2 : struct, ITag
        {
            _positiveTags = MergeTags(_positiveTags, TagSet<T1, T2>.Value);
            return this;
        }

        public SparseQueryBuilder WithTags<T1, T2, T3>()
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag
        {
            _positiveTags = MergeTags(_positiveTags, TagSet<T1, T2, T3>.Value);
            return this;
        }

        public SparseQueryBuilder WithTags<T1, T2, T3, T4>()
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag
            where T4 : struct, ITag
        {
            _positiveTags = MergeTags(_positiveTags, TagSet<T1, T2, T3, T4>.Value);
            return this;
        }

        public SparseQueryBuilder WithTags(TagSet tags)
        {
            _positiveTags = MergeTags(_positiveTags, tags);
            return this;
        }

        public SparseQueryBuilder WithoutTags<T1>()
            where T1 : struct, ITag
        {
            _negativeTags = MergeTags(_negativeTags, TagSet<T1>.Value);
            return this;
        }

        public SparseQueryBuilder WithoutTags<T1, T2>()
            where T1 : struct, ITag
            where T2 : struct, ITag
        {
            _negativeTags = MergeTags(_negativeTags, TagSet<T1, T2>.Value);
            return this;
        }

        public SparseQueryBuilder WithoutTags<T1, T2, T3>()
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag
        {
            _negativeTags = MergeTags(_negativeTags, TagSet<T1, T2, T3>.Value);
            return this;
        }

        public SparseQueryBuilder WithoutTags<T1, T2, T3, T4>()
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag
            where T4 : struct, ITag
        {
            _negativeTags = MergeTags(_negativeTags, TagSet<T1, T2, T3, T4>.Value);
            return this;
        }

        public SparseQueryBuilder WithoutTags(TagSet tags)
        {
            _negativeTags = MergeTags(_negativeTags, tags);
            return this;
        }

        public SparseQueryBuilder WithComponents<T1>()
            where T1 : unmanaged, IEntityComponent
        {
            _positiveComps = _positiveComps.Add(ComponentTypeId<T1>.Value);
            return this;
        }

        public SparseQueryBuilder WithComponents<T1, T2>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
        {
            _positiveComps = _positiveComps.Add(ComponentTypeId<T1>.Value);
            _positiveComps = _positiveComps.Add(ComponentTypeId<T2>.Value);
            return this;
        }

        public SparseQueryBuilder WithComponents<T1, T2, T3>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
        {
            _positiveComps = _positiveComps.Add(ComponentTypeId<T1>.Value);
            _positiveComps = _positiveComps.Add(ComponentTypeId<T2>.Value);
            _positiveComps = _positiveComps.Add(ComponentTypeId<T3>.Value);
            return this;
        }

        public SparseQueryBuilder WithComponents<T1, T2, T3, T4>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
        {
            _positiveComps = _positiveComps.Add(ComponentTypeId<T1>.Value);
            _positiveComps = _positiveComps.Add(ComponentTypeId<T2>.Value);
            _positiveComps = _positiveComps.Add(ComponentTypeId<T3>.Value);
            _positiveComps = _positiveComps.Add(ComponentTypeId<T4>.Value);
            return this;
        }

        public SparseQueryBuilder WithoutComponents<T1>()
            where T1 : unmanaged, IEntityComponent
        {
            _negativeComps = _negativeComps.Add(ComponentTypeId<T1>.Value);
            return this;
        }

        public SparseQueryBuilder WithoutComponents<T1, T2>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
        {
            _negativeComps = _negativeComps.Add(ComponentTypeId<T1>.Value);
            _negativeComps = _negativeComps.Add(ComponentTypeId<T2>.Value);
            return this;
        }

        public SparseQueryBuilder WithoutComponents<T1, T2, T3>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
        {
            _negativeComps = _negativeComps.Add(ComponentTypeId<T1>.Value);
            _negativeComps = _negativeComps.Add(ComponentTypeId<T2>.Value);
            _negativeComps = _negativeComps.Add(ComponentTypeId<T3>.Value);
            return this;
        }

        public SparseQueryBuilder WithoutComponents<T1, T2, T3, T4>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
        {
            _negativeComps = _negativeComps.Add(ComponentTypeId<T1>.Value);
            _negativeComps = _negativeComps.Add(ComponentTypeId<T2>.Value);
            _negativeComps = _negativeComps.Add(ComponentTypeId<T3>.Value);
            _negativeComps = _negativeComps.Add(ComponentTypeId<T4>.Value);
            return this;
        }

        public readonly SparseGroupSliceIterator GroupSlices()
        {
            var groups = ResolveGroups();
            return new SparseGroupSliceIterator(_world, groups, _set);
        }

        public readonly int Count()
        {
            var groups = ResolveGroups();
            var set = _world.GetSetGroupLookup(_set);
            int count = 0;
            for (int i = 0; i < groups.Count; i++)
            {
                if (set.TryGetGroupEntry(groups[i], out var entry))
                    count += entry.Count;
            }
            return count;
        }

        public readonly EntityAccessor Single()
        {
            return new EntityAccessor(_world, SingleEntityIndex());
        }

        public readonly bool TrySingle(out EntityAccessor entityRef)
        {
            if (!TrySingleEntityIndex(out var entityIndex))
            {
                entityRef = default;
                return false;
            }
            entityRef = new EntityAccessor(_world, entityIndex);
            return true;
        }

        public readonly EntityIndex SingleEntityIndex()
        {
            var iter = CreateIterator();

            var movedFirst = iter.MoveNext();
            Assert.That(movedFirst, "Query matched no entities");
            var result = iter.Current;
            var movedSecond = iter.MoveNext();
            Assert.That(!movedSecond, "Query matched multiple entities, expected exactly one");

            return result;
        }

        public readonly bool TrySingleEntityIndex(out EntityIndex entityIndex)
        {
            var iter = CreateIterator();

            if (!iter.MoveNext())
            {
                entityIndex = default;
                return false;
            }

            var first = iter.Current;
            if (iter.MoveNext())
            {
                // Multiple matches — caller asked for a single, so don't hand back the first.
                entityIndex = default;
                return false;
            }

            entityIndex = first;
            return true;
        }

        public readonly QueryIterator EntityIndices()
        {
            return CreateIterator();
        }

        public readonly QueryIterator CreateIterator()
        {
            var groups = ResolveGroups();
            return new QueryIterator(_world, groups, _set);
        }

        readonly ReadOnlyFastList<Group> ResolveGroups()
        {
            var key = new GroupQueryKey(
                _positiveTags,
                _negativeTags,
                _positiveComps,
                _negativeComps
            );

            return _world.WorldInfo.QueryEngine.ResolveGroups(key);
        }

        static TagSet MergeTags(TagSet existing, TagSet addition)
        {
            Assert.That(!addition.IsNull);
            return existing.IsNull ? addition : existing.CombineWith(addition);
        }
    }
}
