using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using Trecs.Collections;

namespace Trecs.Internal
{
    struct GroupsList
    {
        public static GroupsList Init()
        {
            var group = new GroupsList();

            group._groups = new FastList<GroupIndex>();
            group._sets = new HashSet<GroupIndex>();

            return group;
        }

        public void Reset()
        {
            _sets.Clear();
        }

        public void AddRange(GroupIndex[] groupsToAdd, int length)
        {
            for (var i = 0; i < length; i++)
                _sets.Add(groupsToAdd[i]);
        }

        public void Add(GroupIndex group)
        {
            _sets.Add(group);
        }

        public void Exclude(GroupIndex[] groupsToIgnore, int length)
        {
            for (var i = 0; i < length; i++)
                _sets.Remove(groupsToIgnore[i]);
        }

        public void Exclude(GroupIndex groupsToIgnore)
        {
            _sets.Remove(groupsToIgnore);
        }

        public void Resize(int preparecount)
        {
            _groups.EnsureCapacity(preparecount);
        }

        public FastList<GroupIndex> Evaluate()
        {
            _groups.Clear();

            foreach (var item in _sets)
            {
                _groups.Add(item);
            }

            return _groups;
        }

        FastList<GroupIndex> _groups;
        HashSet<GroupIndex> _sets;
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public ref struct QueryGroups
    {
        static readonly ThreadLocal<GroupsList> groups;

        static QueryGroups()
        {
            groups = new ThreadLocal<GroupsList>(GroupsList.Init);
        }

        public QueryGroups(LocalReadOnlyFastList<GroupIndex> groups)
        {
            var groupsValue = QueryGroups.groups.Value;

            groupsValue.Reset();
            groupsValue.AddRange(groups.ToArrayFast(out var count), count);
        }

        public QueryGroups(GroupIndex group)
        {
            var groupsValue = groups.Value;

            groupsValue.Reset();
            groupsValue.Add(group);
        }

        public QueryGroups(int preparecount)
        {
            var groupsValue = groups.Value;

            groupsValue.Reset();
            groupsValue.Resize(preparecount);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public QueryGroups Union(GroupIndex group)
        {
            var groupsValue = groups.Value;

            groupsValue.Add(group);

            return this;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public QueryGroups Union(LocalReadOnlyFastList<GroupIndex> groups)
        {
            var groupsValue = QueryGroups.groups.Value;

            groupsValue.AddRange(groups.ToArrayFast(out var count), count);

            return this;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public QueryGroups Except(GroupIndex group)
        {
            var groupsValue = groups.Value;

            groupsValue.Exclude(group);

            return this;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public QueryGroups Except(GroupIndex[] groupsToIgnore)
        {
            var groupsValue = groups.Value;

            groupsValue.Exclude(groupsToIgnore, groupsToIgnore.Length);

            return this;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public QueryGroups Except(LocalReadOnlyFastList<GroupIndex> groupsToIgnore)
        {
            var groupsValue = groups.Value;

            groupsValue.Exclude(groupsToIgnore.ToArrayFast(out var count), count);

            return this;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public QueryGroups Except(FastList<GroupIndex> groupsToIgnore)
        {
            var groupsValue = groups.Value;

            groupsValue.Exclude(groupsToIgnore.ToArrayFast(out var count), count);

            return this;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public QueryGroups Except(ReadOnlyFastList<GroupIndex> groupsToIgnore)
        {
            var groupsValue = groups.Value;

            groupsValue.Exclude(groupsToIgnore.ToArrayFast(out var count), count);

            return this;
        }

        public QueryResult Evaluate()
        {
            var groupsValue = groups.Value;

            return new QueryResult(groupsValue.Evaluate());
        }

        public void Evaluate(FastList<GroupIndex> group)
        {
            var groupsValue = groups.Value;

            groupsValue.Evaluate().CopyTo(group.ToArrayFast(out var count), count);
        }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly ref struct QueryResult
    {
        public QueryResult(FastList<GroupIndex> group)
        {
            _group = group;
        }

        public LocalReadOnlyFastList<GroupIndex> Result => _group;

        readonly ReadOnlyFastList<GroupIndex> _group;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Count<T>(EntityQuerier entitiesQuerier)
            where T : unmanaged, IEntityComponent
        {
            var count = 0;

            var groupsCount = Result.Count;
            for (var i = 0; i < groupsCount; ++i)
                count += entitiesQuerier.Count<T>(Result[i]);

            return count;
        }

        public int Max<T>(EntityQuerier entitiesQuerier)
            where T : unmanaged, IEntityComponent
        {
            var max = 0;

            var groupsCount = Result.Count;
            for (var i = 0; i < groupsCount; ++i)
            {
                var count = entitiesQuerier.Count<T>(Result[i]);
                if (count > max)
                    max = count;
            }

            return max;
        }
    }
}
