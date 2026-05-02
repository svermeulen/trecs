using Trecs.Collections;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Fluent builder for dense entity queries. Chain <c>WithTags</c>, <c>WithoutTags</c>,
    /// <c>WithComponents</c>, and <c>WithoutComponents</c> to narrow matching groups, then
    /// terminate with <see cref="EntityIndices"/>, <see cref="GroupSlices"/>, <see cref="Single"/>,
    /// or <see cref="Count"/> to consume results. Call <see cref="InSet{T}"/> to switch to
    /// sparse (set-filtered) iteration via <see cref="SparseQueryBuilder"/>.
    /// Obtained from <see cref="WorldAccessor.Query"/>.
    /// </summary>
    public ref struct QueryBuilder
    {
        readonly WorldAccessor _world;

        TagSet _positiveTags;
        TagSet _negativeTags;
        ComponentTypeIdSet _positiveComps;
        ComponentTypeIdSet _negativeComps;

        internal QueryBuilder(WorldAccessor world)
        {
            _world = world;
            _positiveTags = default;
            _negativeTags = default;
            _positiveComps = default;
            _negativeComps = default;
        }

        public readonly WorldAccessor World => _world;

        /// <summary>
        /// True when this builder has at least one positive tag, negative tag, positive
        /// component, or negative component constraint applied. Used by source-generated
        /// iteration entry points to fail loud when called with no criteria (the iteration
        /// would otherwise walk every group in the world and crash on the first GetBuffer).
        /// </summary>
        public readonly bool HasAnyCriteria =>
            !_positiveTags.IsNull
            || !_negativeTags.IsNull
            || !_positiveComps.IsNull
            || !_negativeComps.IsNull;

        public QueryBuilder WithTags<T1>()
            where T1 : struct, ITag
        {
            _positiveTags = MergeTags(_positiveTags, TagSet<T1>.Value);
            return this;
        }

        public QueryBuilder WithTags<T1, T2>()
            where T1 : struct, ITag
            where T2 : struct, ITag
        {
            _positiveTags = MergeTags(_positiveTags, TagSet<T1, T2>.Value);
            return this;
        }

        public QueryBuilder WithTags<T1, T2, T3>()
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag
        {
            _positiveTags = MergeTags(_positiveTags, TagSet<T1, T2, T3>.Value);
            return this;
        }

        public QueryBuilder WithTags<T1, T2, T3, T4>()
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag
            where T4 : struct, ITag
        {
            _positiveTags = MergeTags(_positiveTags, TagSet<T1, T2, T3, T4>.Value);
            return this;
        }

        public QueryBuilder WithTags(TagSet tags)
        {
            _positiveTags = MergeTags(_positiveTags, tags);
            return this;
        }

        public QueryBuilder WithoutTags<T1>()
            where T1 : struct, ITag
        {
            _negativeTags = MergeTags(_negativeTags, TagSet<T1>.Value);
            return this;
        }

        public QueryBuilder WithoutTags<T1, T2>()
            where T1 : struct, ITag
            where T2 : struct, ITag
        {
            _negativeTags = MergeTags(_negativeTags, TagSet<T1, T2>.Value);
            return this;
        }

        public QueryBuilder WithoutTags<T1, T2, T3>()
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag
        {
            _negativeTags = MergeTags(_negativeTags, TagSet<T1, T2, T3>.Value);
            return this;
        }

        public QueryBuilder WithoutTags<T1, T2, T3, T4>()
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag
            where T4 : struct, ITag
        {
            _negativeTags = MergeTags(_negativeTags, TagSet<T1, T2, T3, T4>.Value);
            return this;
        }

        public QueryBuilder WithoutTags(TagSet tags)
        {
            _negativeTags = MergeTags(_negativeTags, tags);
            return this;
        }

        public QueryBuilder WithComponents<T1>()
            where T1 : unmanaged, IEntityComponent
        {
            _positiveComps = _positiveComps.Add(ComponentTypeId<T1>.Value);
            return this;
        }

        public QueryBuilder WithComponents<T1, T2>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
        {
            _positiveComps = _positiveComps.Add(ComponentTypeId<T1>.Value);
            _positiveComps = _positiveComps.Add(ComponentTypeId<T2>.Value);
            return this;
        }

        public QueryBuilder WithComponents<T1, T2, T3>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
        {
            _positiveComps = _positiveComps.Add(ComponentTypeId<T1>.Value);
            _positiveComps = _positiveComps.Add(ComponentTypeId<T2>.Value);
            _positiveComps = _positiveComps.Add(ComponentTypeId<T3>.Value);
            return this;
        }

        public QueryBuilder WithComponents<T1, T2, T3, T4>()
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

        public QueryBuilder WithoutComponents<T1>()
            where T1 : unmanaged, IEntityComponent
        {
            _negativeComps = _negativeComps.Add(ComponentTypeId<T1>.Value);
            return this;
        }

        public QueryBuilder WithoutComponents<T1, T2>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
        {
            _negativeComps = _negativeComps.Add(ComponentTypeId<T1>.Value);
            _negativeComps = _negativeComps.Add(ComponentTypeId<T2>.Value);
            return this;
        }

        public QueryBuilder WithoutComponents<T1, T2, T3>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
        {
            _negativeComps = _negativeComps.Add(ComponentTypeId<T1>.Value);
            _negativeComps = _negativeComps.Add(ComponentTypeId<T2>.Value);
            _negativeComps = _negativeComps.Add(ComponentTypeId<T3>.Value);
            return this;
        }

        public QueryBuilder WithoutComponents<T1, T2, T3, T4>()
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

        /// <summary>
        /// Transitions to a <see cref="SparseQueryBuilder"/> with the given set.
        /// Returns a different builder type because set-filtered iteration is fundamentally
        /// sparse (walking set indices) rather than dense (walking all entities in a group).
        /// Only one set can be applied per query. If you need to intersect multiple sets,
        /// query with one set and check <c>SetAccessor&lt;T&gt;.Exists()</c> for the others
        /// inside the loop.
        /// </summary>
        public readonly SparseQueryBuilder InSet<T>()
            where T : struct, IEntitySet
        {
            return new SparseQueryBuilder(
                _world,
                _positiveTags,
                _negativeTags,
                _positiveComps,
                _negativeComps,
                EntitySet<T>.Value.Id
            );
        }

        public readonly SparseQueryBuilder InSet(EntitySet entitySet)
        {
            return new SparseQueryBuilder(
                _world,
                _positiveTags,
                _negativeTags,
                _positiveComps,
                _negativeComps,
                entitySet.Id
            );
        }

        public readonly SparseQueryBuilder InSet(SetId setId)
        {
            return new SparseQueryBuilder(
                _world,
                _positiveTags,
                _negativeTags,
                _positiveComps,
                _negativeComps,
                setId
            );
        }

        public readonly QueryIterator EntityIndices()
        {
            AssertHasAnyCriteria();
            return CreateIterator();
        }

        /// <summary>
        /// Returns a dense group slice iterator for queries without set filters.
        /// Each slice has GroupIndex, Count, and an identity indexer.
        /// </summary>
        public readonly DenseGroupSliceIterator GroupSlices()
        {
            AssertHasAnyCriteria();
            var groups = ResolveGroups();
            return new DenseGroupSliceIterator(_world, groups);
        }

        public readonly ReadOnlyFastList<GroupIndex> Groups()
        {
            AssertHasAnyCriteria();
            return ResolveGroups();
        }

        public readonly int Count()
        {
            AssertHasAnyCriteria();
            var groups = ResolveGroups();
            return _world.CountEntitiesInGroups(groups);
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
            AssertHasAnyCriteria();
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
            AssertHasAnyCriteria();
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

        readonly void AssertHasAnyCriteria()
        {
            Require.That(
                HasAnyCriteria,
                "Query has no criteria — add at least one WithTags / WithoutTags / WithComponents / WithoutComponents constraint before calling a terminator"
            );
        }

        internal readonly ReadOnlyFastList<GroupIndex> ResolveGroups()
        {
            var key = new GroupQueryKey(
                _positiveTags,
                _negativeTags,
                _positiveComps,
                _negativeComps
            );

            return _world.WorldInfo.QueryEngine.ResolveGroups(key);
        }

        public readonly QueryIterator CreateIterator()
        {
            var groups = ResolveGroups();
            return new QueryIterator(_world, groups);
        }

        static TagSet MergeTags(TagSet existing, TagSet addition)
        {
            Assert.That(!addition.IsNull);
            return existing.IsNull ? addition : existing.CombineWith(addition);
        }
    }
}
