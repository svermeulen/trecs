using System.Collections.Generic;
using System.Text;
using Trecs.Internal;

namespace Trecs
{
    public static class ComponentTypeIdSetRegistry
    {
        static readonly Dictionary<int, IReadOnlyList<ComponentId>> _sets = new();
        static readonly Dictionary<int, string> _debugStrings = new();

        public static ComponentTypeIdSet FromSingle(ComponentId componentId)
        {
            Assert.That(UnityThreadUtil.IsMainThread);
            int id = componentId.Value;

            if (id == 0)
            {
                id = 1; // reserve 0 for null
            }

            if (_sets.TryGetValue(id, out var existing))
            {
#if TRECS_INTERNAL_CHECKS && DEBUG
                ValidateSet(id, existing, new[] { componentId });
#endif
            }
            else
            {
                _sets.Add(id, new[] { componentId });
            }

            return new ComponentTypeIdSet(id);
        }

        public static ComponentTypeIdSet AddComponent(
            ComponentTypeIdSet existing,
            ComponentId componentId
        )
        {
            Assert.That(UnityThreadUtil.IsMainThread);
            var existingComponents = _sets[existing.Id];

            // Check if already present
            foreach (var c in existingComponents)
            {
                if (c.Equals(componentId))
                {
                    return existing;
                }
            }

            int newId = existing.Id ^ componentId.Value;

            if (newId == 0)
            {
                newId = 1; // reserve 0 for null
            }

            if (_sets.TryGetValue(newId, out var newExistingSet))
            {
#if TRECS_INTERNAL_CHECKS && DEBUG
                var newList = new ComponentId[existingComponents.Count + 1];

                for (int i = 0; i < existingComponents.Count; i++)
                {
                    newList[i] = existingComponents[i];
                }
                newList[existingComponents.Count] = componentId;
                ValidateSet(newId, newExistingSet, newList);
#endif
            }
            else
            {
                var newList = new ComponentId[existingComponents.Count + 1];
                for (int i = 0; i < existingComponents.Count; i++)
                    newList[i] = existingComponents[i];
                newList[existingComponents.Count] = componentId;
                _sets[newId] = newList;
            }

            return new ComponentTypeIdSet(newId);
        }

        public static IReadOnlyList<ComponentId> GetComponents(ComponentTypeIdSet set)
        {
            Assert.That(UnityThreadUtil.IsMainThread);
            Assert.That(!set.IsNull, "Cannot get components from null ComponentTypeIdSet");

            if (_sets.TryGetValue(set.Id, out var existing))
            {
                return existing;
            }

            throw new KeyNotFoundException($"ComponentTypeIdSet with ID {set.Id} not registered");
        }

        public static string SetToString(ComponentTypeIdSet set)
        {
            Assert.That(UnityThreadUtil.IsMainThread);
            if (set.IsNull)
            {
                return "Null";
            }

            if (_debugStrings.TryGetValue(set.Id, out var cached))
            {
                return cached;
            }

            var components = GetComponents(set);
            var sb = new StringBuilder();
            sb.Append("ComponentTypeIdSet(");
            for (int i = 0; i < components.Count; i++)
            {
                if (i > 0)
                    sb.Append(", ");
                var type = TypeIdProvider.GetTypeFromId(components[i].Value);
                sb.Append(type.Name);
            }
            sb.Append(")");

            var result = sb.ToString();
            _debugStrings.Add(set.Id, result);
            return result;
        }

#if TRECS_INTERNAL_CHECKS && DEBUG
        static void ValidateSet(
            int id,
            IReadOnlyList<ComponentId> existing,
            IReadOnlyList<ComponentId> components
        )
        {
            Assert.That(
                existing.Count == components.Count,
                "ComponentTypeIdSet XOR hash collision detected! ID {} maps to sets of different sizes ({} vs {})",
                id,
                existing.Count,
                components.Count
            );

            // Check all components in the new set exist in the cached set
            foreach (var c in components)
            {
                bool found = false;
                foreach (var e in existing)
                {
                    if (e.Equals(c))
                    {
                        found = true;
                        break;
                    }
                }

                Assert.That(
                    found,
                    "ComponentTypeIdSet XOR hash collision detected! ID {} has mismatched components",
                    id
                );
            }
        }
#endif
    }
}
