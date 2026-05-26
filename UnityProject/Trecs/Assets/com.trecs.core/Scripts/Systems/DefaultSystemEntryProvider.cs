using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Trecs.Collections;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal sealed class DefaultSystemEntryProvider : ISystemEntryProvider
    {
        readonly TrecsLog _log;

        readonly List<SystemOrderConstraint> _constraints;
        readonly WorldAccessorRegistry _accessorRegistry;

        public DefaultSystemEntryProvider(
            TrecsLog log,
            List<SystemOrderConstraint> constraints,
            WorldAccessorRegistry accessorRegistry
        )
        {
            _log = log;
            _constraints = constraints;
            _accessorRegistry = accessorRegistry;
        }

        void AddConstraint(
            Type before,
            Type after,
            IterableDictionary<RefKey<Type>, IterableHashSet<RefKey<Type>>> directDeps
        )
        {
            if (before == after)
            {
                return;
            }

            _log.Trace("Added system constraint {0} -> {1}", before, after);

            if (!directDeps.ContainsKey(after))
            {
                directDeps[after] = new();
            }

            directDeps[after].Add(before);
        }

        void AddSystemAttributeContraints(
            ISystem system,
            IterableDictionary<RefKey<Type>, IterableHashSet<RefKey<Type>>> directDeps
        )
        {
            _log.Trace("Adding system constraint for system {0}", system.GetType());

            var systemType = system.GetType();

            var beforeAttributes = systemType.GetCustomAttributes(
                typeof(ExecuteBeforeAttribute),
                true
            );
            var afterAttributes = systemType.GetCustomAttributes(
                typeof(ExecuteAfterAttribute),
                true
            );

            foreach (ExecuteBeforeAttribute beforeAttribute in beforeAttributes)
            {
                foreach (var systemTypeBefore in beforeAttribute.Systems)
                {
                    AddConstraint(systemType, systemTypeBefore, directDeps);
                }
            }

            foreach (ExecuteAfterAttribute afterAttribute in afterAttributes)
            {
                foreach (var systemTypeAfter in afterAttribute.Systems)
                {
                    AddConstraint(systemTypeAfter, systemType, directDeps);
                }
            }
        }

        IterableDictionary<RefKey<Type>, IterableHashSet<RefKey<Type>>> InitializeSystemConstraints(
            IReadOnlyList<ISystem> systems
        )
        {
            var directDeps = new IterableDictionary<RefKey<Type>, IterableHashSet<RefKey<Type>>>();

            foreach (var system in systems)
            {
                AddSystemAttributeContraints(system, directDeps);
            }

            foreach (var constraint in _constraints)
            {
                var systemOrder = constraint.FlattenSystemOrder();

                TrecsDebugAssert.That(systemOrder.Count > 1);

                var previousType = systemOrder[0];

                // Note here that we don't need to have every class depend directly on
                // every previous class, because these deps are just used to choose
                // execution order and not used for job deps
                // We can assume also all system types are installed since otherwise
                // we assert below
                for (int i = 1; i < systemOrder.Count; i++)
                {
                    var systemType = systemOrder[i];
                    AddConstraint(previousType, systemType, directDeps);
                    previousType = systemType;
                }
            }

            return directDeps;
        }

        public IReadOnlyList<SystemEntry> GetSystemEntries(World _, IReadOnlyList<ISystem> systems)
        {
            var directDeps = InitializeSystemConstraints(systems);

            var result = new List<SystemEntry>();

            var typeToIndexMap = new IterableDictionary<RefKey<Type>, List<int>>();

            // We assume here there is only ever one instance of each system
            for (int i = 0; i < systems.Count; i++)
            {
                var system = systems[i];
                var systemType = system.GetType();

                if (!typeToIndexMap.TryGetValue(systemType, out var indices))
                {
                    indices = new();
                    typeToIndexMap.Add(systemType, indices);
                }

                indices.Add(i);
            }

            if (TrecsDebugAssert.IsEnabled)
            {
                foreach (var systemType in directDeps.Keys)
                {
                    // Need to do this because of the logic above that only constrains order to previous
                    // system in a given constraint list
                    // Support base class / interface matching: a constraint referencing a base type
                    // is satisfied if any registered system is assignable to that type
                    bool found = typeToIndexMap.ContainsKey(systemType);
                    if (!found)
                    {
                        foreach (var t in typeToIndexMap.Keys)
                        {
                            if (systemType.Value.IsAssignableFrom(t.Value))
                            {
                                found = true;
                                break;
                            }
                        }
                    }

                    TrecsDebugAssert.That(
                        found,
                        "Added system constraint for system {0} which is not in the system list",
                        systemType
                    );
                }
            }

            for (int i = 0; i < systems.Count; i++)
            {
                var system = systems[i];
                var systemType = system.GetType();

                // Gather dependency types from this type and all its base types,
                // so constraints placed on a base class apply to concrete subclasses
                var allDirectDepsTypes = new IterableHashSet<RefKey<Type>>();
                for (var type = systemType; type != null; type = type.BaseType)
                {
                    if (directDeps.TryGetValue(type, out var depsForType))
                    {
                        allDirectDepsTypes.UnionWith(depsForType);
                    }
                }

                List<int> directDepsIndices;

                if (allDirectDepsTypes.Count > 0)
                {
#if DEBUG
                    foreach (var type in allDirectDepsTypes)
                    {
                        bool depFound = false;
                        foreach (var t in typeToIndexMap.Keys)
                        {
                            if (type.Value.IsAssignableFrom(t.Value))
                            {
                                depFound = true;
                                break;
                            }
                        }

                        if (!depFound)
                        {
                            _log.Warning(
                                "System {0} depends on system {1} which is not in the system list",
                                systemType,
                                type
                            );
                        }
                    }
#endif

                    directDepsIndices = new List<int>();
                    foreach (var depType in allDirectDepsTypes)
                    {
                        foreach (var kvp in typeToIndexMap)
                        {
                            if (depType.Value.IsAssignableFrom(kvp.Key.Value))
                            {
                                foreach (var idx in kvp.Value)
                                {
                                    if (!directDepsIndices.Contains(idx))
                                    {
                                        directDepsIndices.Add(idx);
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    directDepsIndices = new List<int>();
                }

                var executeInAttribute = (ExecuteInAttribute)
                    systemType
                        .GetCustomAttributes(typeof(ExecuteInAttribute), true)
                        .SingleOrDefault();

                var phase = executeInAttribute?.Phase ?? SystemPhase.Fixed;

                var priorityAttribute = (ExecutePriorityAttribute)
                    systemType
                        .GetCustomAttributes(typeof(ExecutePriorityAttribute), true)
                        .SingleOrDefault();

                var accessor = ((ISystemInternal)system).World;

                result.Add(
                    new SystemEntry(
                        system,
                        directDepsIndices,
                        phase: phase,
                        accessor: accessor,
                        debugName: systemType.GetPrettyName(),
                        executionPriority: priorityAttribute?.Priority
                    )
                );
            }

            return result;
        }
    }
}
