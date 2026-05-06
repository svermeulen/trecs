using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Trecs.Collections;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public class DefaultSystemMetadataProvider : ISystemMetadataProvider
    {
        static readonly TrecsLog _log = new(nameof(DefaultSystemMetadataProvider));

        readonly List<SystemOrderConstraint> _constraints;
        readonly WorldAccessorRegistry _accessorRegistry;

        public DefaultSystemMetadataProvider(
            List<SystemOrderConstraint> constraints,
            WorldAccessorRegistry accessorRegistry
        )
        {
            _constraints = constraints;
            _accessorRegistry = accessorRegistry;
        }

        void AddConstraint(Type before, Type after, DenseDictionary<Type, HashSet<Type>> directDeps)
        {
            if (before == after)
            {
                return;
            }

            _log.Trace("Added system constraint {} -> {}", before, after);

            if (!directDeps.ContainsKey(after))
            {
                directDeps[after] = new HashSet<Type>();
            }

            directDeps[after].Add(before);
        }

        void AddSystemAttributeContraints(
            ISystem system,
            DenseDictionary<Type, HashSet<Type>> directDeps
        )
        {
            _log.Trace("Adding system constraint for system {}", system.GetType());

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

        DenseDictionary<Type, HashSet<Type>> InitializeSystemConstraints(
            IReadOnlyList<ISystem> systems
        )
        {
            var directDeps = new DenseDictionary<Type, HashSet<Type>>();

            foreach (var system in systems)
            {
                AddSystemAttributeContraints(system, directDeps);
            }

            foreach (var constraint in _constraints)
            {
                var systemOrder = constraint.FlattenSystemOrder();

                Assert.That(systemOrder.Count > 1);

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

        public IReadOnlyList<SystemMetadata> GetSystemMetadata(
            World _,
            IReadOnlyList<ISystem> systems
        )
        {
            var directDeps = InitializeSystemConstraints(systems);

            var result = new List<SystemMetadata>();

            var typeToIndexMap = new Dictionary<Type, List<int>>();

            // We assume here there is only ever once instance of each system
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

            foreach (var systemType in directDeps.Keys)
            {
                // Need to do this because of the logic above that only constrains order to previous
                // system in a given constraint list
                // Support base class / interface matching: a constraint referencing a base type
                // is satisfied if any registered system is assignable to that type
                Assert.That(
                    typeToIndexMap.ContainsKey(systemType)
                        || typeToIndexMap.Keys.Any(t => systemType.IsAssignableFrom(t)),
                    "Added system constraint for system {} which is not in the system list",
                    systemType
                );
            }

            for (int i = 0; i < systems.Count; i++)
            {
                var system = systems[i];
                var systemType = system.GetType();

                // Gather dependency types from this type and all its base types,
                // so constraints placed on a base class apply to concrete subclasses
                var allDirectDepsTypes = new HashSet<Type>();
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
                        if (!typeToIndexMap.Keys.Any(t => type.IsAssignableFrom(t)))
                        {
                            _log.Warning(
                                "System {} depends on system {} which is not in the system list",
                                systemType,
                                type
                            );
                        }
                    }
#endif

                    // Resolve each dependency type to indices, supporting base class matching:
                    // a dependency on BaseType matches any registered system that inherits from it
                    directDepsIndices = allDirectDepsTypes
                        .SelectMany(depType =>
                            typeToIndexMap
                                .Where(kvp => depType.IsAssignableFrom(kvp.Key))
                                .SelectMany(kvp => kvp.Value)
                        )
                        .Distinct()
                        .ToList();
                }
                else
                {
                    directDepsIndices = new List<int>();
                }

                var phaseAttribute = (PhaseAttribute)
                    systemType.GetCustomAttributes(typeof(PhaseAttribute), true).SingleOrDefault();

                var phase = phaseAttribute?.Phase ?? SystemPhase.Fixed;

                var priorityAttribute = (ExecutePriorityAttribute)
                    systemType
                        .GetCustomAttributes(typeof(ExecutePriorityAttribute), true)
                        .SingleOrDefault();

                var accessor = ((ISystemInternal)system).World;

                result.Add(
                    new SystemMetadata(
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
