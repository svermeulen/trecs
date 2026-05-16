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

        /// <summary>
        /// Returns the single matching entity as a stable <see cref="EntityHandle"/>.
        /// </summary>
        public readonly EntityHandle SingleHandle() => SingleIndex().ToHandle(_world);

        /// <summary>
        /// Resolves the single matching entity to an <see cref="EntityHandle"/>,
        /// returning false on zero or multiple matches.
        /// </summary>
        public readonly bool TrySingleHandle(out EntityHandle entityHandle)
        {
            if (!TrySingleIndex(out var entityIndex))
            {
                entityHandle = default;
                return false;
            }
            entityHandle = entityIndex.ToHandle(_world);
            return true;
        }

        /// <summary>
        /// Hot-loop variant of <see cref="SingleHandle"/> — returns a transient
        /// <see cref="EntityIndex"/> without the handle conversion.
        /// </summary>
        public readonly EntityIndex SingleIndex()
        {
            var iter = CreateIterator();

            var movedFirst = iter.MoveNext();
            TrecsAssert.That(movedFirst, "Query matched no entities");
            var result = iter.Current;
            var movedSecond = iter.MoveNext();
            TrecsAssert.That(!movedSecond, "Query matched multiple entities, expected exactly one");

            return result;
        }

        /// <summary>
        /// Hot-loop variant of <see cref="TrySingleHandle"/>.
        /// </summary>
        public readonly bool TrySingleIndex(out EntityIndex entityIndex)
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

        /// <summary>
        /// Hot-loop iterator yielding transient <see cref="EntityIndex"/> values.
        /// </summary>
        public readonly IndexQueryIterator Indices()
        {
            return CreateIterator();
        }

        /// <summary>
        /// Returns an iterator that yields a stable <see cref="EntityHandle"/> per matched entity.
        /// </summary>
        public readonly HandleQueryIterator Handles()
        {
            return new HandleQueryIterator(CreateIterator(), _world);
        }

        internal readonly IndexQueryIterator CreateIterator()
        {
            var groups = ResolveGroups();
            return new IndexQueryIterator(_world, groups, _set);
        }

        readonly ReadOnlyFastList<GroupIndex> ResolveGroups()
        {
            var key = new GroupQueryKey(
                _positiveTags,
                _negativeTags,
                _positiveComps,
                _negativeComps
            );

            var groups = _world.WorldInfo.QueryEngine.ResolveGroups(key);

            _world.AssertNoVariableUpdateOnlyGroupsForFixedRole(groups);

            return groups;
        }

        static TagSet MergeTags(TagSet existing, TagSet addition)
        {
            TrecsAssert.That(!addition.IsNull);
            return existing.IsNull ? addition : existing.CombineWith(addition);
        }
    }
}
