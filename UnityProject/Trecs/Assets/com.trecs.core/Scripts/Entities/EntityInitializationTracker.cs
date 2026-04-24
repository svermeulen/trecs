#if DEBUG && !TRECS_IS_PROFILING

using System.Collections.Generic;
using System.ComponentModel;
using System.Text;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public class EntityInitializationTracker
    {
        struct TrackedEntity
        {
            public GroupIndex GroupIndex;
            public string DescriptorName;
            public string CallerFile;
            public int CallerLine;
            public IComponentBuilder[] ComponentBuilders;
            public HashSet<ComponentId> InitializedComponents;
            public bool Validated;
        }

        readonly List<TrackedEntity> _entries = new();
        readonly Stack<HashSet<ComponentId>> _hashSetPool = new();

        public int Register(
            GroupIndex group,
            IComponentBuilder[] builders,
            string descriptorName,
            string callerFile,
            int callerLine
        )
        {
            var initializedSet =
                _hashSetPool.Count > 0 ? _hashSetPool.Pop() : new HashSet<ComponentId>();
            initializedSet.Clear();

            var id = _entries.Count;
            _entries.Add(
                new TrackedEntity
                {
                    GroupIndex = group,
                    DescriptorName = descriptorName,
                    CallerFile = callerFile,
                    CallerLine = callerLine,
                    ComponentBuilders = builders,
                    InitializedComponents = initializedSet,
                    Validated = false,
                }
            );
            return id;
        }

        public void MarkComponentSet(int id, ComponentId componentId, GroupIndex group)
        {
            if (!_entries[id].InitializedComponents.Add(componentId))
            {
                throw new TrecsException(
                    $"Component type '{TypeIdProvider.GetTypeFromId(componentId.Value).GetPrettyName()}' "
                        + $"has already been initialized for entity initializer, while adding to group {group}"
                );
            }
        }

        public void ValidateEntry(int id)
        {
            var entry = _entries[id];
            var missingComponents = FindMissingComponents(entry);

            if (missingComponents != null)
            {
                throw new TrecsException(
                    "Missing initial values for the following components:\n" + missingComponents
                );
            }

            entry.Validated = true;
            _entries[id] = entry;
        }

        public void ValidateAllPending()
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];

                if (entry.Validated)
                    continue;

                var missingComponents = FindMissingComponents(entry);

                if (missingComponents != null)
                {
                    throw new TrecsException(
                        $"Entity created at {entry.CallerFile}:{entry.CallerLine} "
                            + $"(group {entry.GroupIndex}, descriptor: {entry.DescriptorName}) "
                            + "is missing initial values for the following components:\n"
                            + missingComponents
                    );
                }
            }
        }

        static StringBuilder FindMissingComponents(in TrackedEntity entry)
        {
            StringBuilder missing = null;

            for (int i = 0; i < entry.ComponentBuilders.Length; i++)
            {
                var builder = entry.ComponentBuilders[i];

                if (builder.HasUserProvidedPrototype)
                    continue;

                if (entry.InitializedComponents.Contains(builder.ComponentId))
                    continue;

                missing ??= new StringBuilder();
                missing.AppendLine(builder.ComponentType.FullName);
            }

            return missing;
        }

        public void Clear()
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                var set = _entries[i].InitializedComponents;
                if (set != null)
                {
                    set.Clear();
                    _hashSetPool.Push(set);
                }
            }

            _entries.Clear();
        }
    }
}
#endif
