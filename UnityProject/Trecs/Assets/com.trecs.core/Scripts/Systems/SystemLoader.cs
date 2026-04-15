using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using Trecs.Collections;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public class SystemLoader
    {
        static readonly TrecsLog _log = new(nameof(SystemLoader));

        readonly ISystemMetadataProvider _metadataProvider;
        readonly WorldInfo _worldDef;
        readonly WorldAccessorRegistry _accessorRegistry;

        bool _isLocked;

        public SystemLoader(
            WorldAccessorRegistry accessorRegistry,
            ISystemMetadataProvider metadataProvider,
            WorldInfo worldDef
        )
        {
            _accessorRegistry = accessorRegistry;
            _metadataProvider = metadataProvider;
            _worldDef = worldDef;
        }

        public List<int> SortFixedSystems(
            List<SystemInfo> allSystems,
            List<int> fixedSystems,
            List<int> globalToLocalIndexMap,
            List<HashSet<int>> systemDepsMap
        )
        {
            var localDepsList = new List<HashSet<int>>(fixedSystems.Count);

            for (int localIndex = 0; localIndex < fixedSystems.Count; localIndex++)
            {
                localDepsList.Add(new HashSet<int>());
            }

            for (
                int systemLocalIndex = 0;
                systemLocalIndex < fixedSystems.Count;
                systemLocalIndex++
            )
            {
                var systemGlobalIndex = fixedSystems[systemLocalIndex];
                var systemInfo = allSystems[systemGlobalIndex];
                var localDeps = localDepsList[systemLocalIndex];

                Assert.That(systemInfo.Metadata.RunPhase == SystemRunPhase.Fixed);

                foreach (var otherGlobalIndex in systemDepsMap[systemGlobalIndex])
                {
                    var otherInfo = allSystems[otherGlobalIndex];

                    if (otherInfo.Metadata.RunPhase == SystemRunPhase.Fixed)
                    {
                        var otherLocalIndex = globalToLocalIndexMap[otherGlobalIndex];
                        Assert.That(fixedSystems[otherLocalIndex] == otherGlobalIndex);
                        localDeps.Add(otherLocalIndex);
                    }
                }
            }

            // Now sort to ensure that every system is after all of its dependencies
            // should throw if this is not possible
            var sortedFixedSystemsLocal = TopologicalSorter.Run(
                fixedSystems,
                // Order by DeclarationIndex just for extra consistency just in case, though may not matter
                globalIndex =>
                    localDepsList[globalToLocalIndexMap[globalIndex]].OrderBy(x => globalIndex),
                globalIndex => GetSystemAndMetaDataOrderBy(allSystems[globalIndex]),
                globalIndex => allSystems[globalIndex].Metadata.DebugName
            );

            Assert.IsEqual(sortedFixedSystemsLocal.Count, fixedSystems.Count);

            var sortedFixedSystemsGlobal = new List<int>(fixedSystems.Count);

            for (int i = 0; i < sortedFixedSystemsLocal.Count; i++)
            {
                var localIndex = sortedFixedSystemsLocal[i];
                var globalIndex = fixedSystems[localIndex];
                var info = allSystems[globalIndex];
                Assert.That(info.Metadata.RunPhase == SystemRunPhase.Fixed);
                sortedFixedSystemsGlobal.Add(globalIndex);
            }

            if (sortedFixedSystemsGlobal.IsEmpty())
            {
                _log.Debug("No fixed systems to run");
            }

            return sortedFixedSystemsGlobal;
        }

        public List<int> SortInputSystems(
            List<SystemInfo> allSystems,
            List<int> inputSystems,
            List<int> globalToLocalIndexMap,
            List<HashSet<int>> systemDepsMap
        )
        {
            var localDepsList = new List<HashSet<int>>(inputSystems.Count);

            for (int localIndex = 0; localIndex < inputSystems.Count; localIndex++)
            {
                localDepsList.Add(new HashSet<int>());
            }

            for (
                int systemLocalIndex = 0;
                systemLocalIndex < inputSystems.Count;
                systemLocalIndex++
            )
            {
                var systemGlobalIndex = inputSystems[systemLocalIndex];
                var systemInfo = allSystems[systemGlobalIndex];
                var localDeps = localDepsList[systemLocalIndex];

                Assert.That(systemInfo.Metadata.RunPhase == SystemRunPhase.Input);

                foreach (var otherGlobalIndex in systemDepsMap[systemGlobalIndex])
                {
                    var otherInfo = allSystems[otherGlobalIndex];

                    if (otherInfo.Metadata.RunPhase == SystemRunPhase.Input)
                    {
                        var otherLocalIndex = globalToLocalIndexMap[otherGlobalIndex];
                        Assert.That(inputSystems[otherLocalIndex] == otherGlobalIndex);
                        localDeps.Add(otherLocalIndex);
                    }
                }
            }

            // Now sort to ensure that every system is after all of its dependencies
            // should throw if this is not possible
            var sortedInputSystemsLocal = TopologicalSorter.Run(
                inputSystems,
                // Order by DeclarationIndex just for extra consistency just in case, though may not matter
                globalIndex =>
                    localDepsList[globalToLocalIndexMap[globalIndex]].OrderBy(x => globalIndex),
                globalIndex => GetSystemAndMetaDataOrderBy(allSystems[globalIndex]),
                globalIndex => allSystems[globalIndex].Metadata.DebugName
            );

            Assert.IsEqual(sortedInputSystemsLocal.Count, inputSystems.Count);

            var sortedInputSystemsGlobal = new List<int>(inputSystems.Count);

            for (int i = 0; i < sortedInputSystemsLocal.Count; i++)
            {
                var localIndex = sortedInputSystemsLocal[i];
                var globalIndex = inputSystems[localIndex];
                var info = allSystems[globalIndex];
                Assert.That(info.Metadata.RunPhase == SystemRunPhase.Input);
                sortedInputSystemsGlobal.Add(globalIndex);
            }

            if (sortedInputSystemsGlobal.IsEmpty())
            {
                _log.Debug("No input systems to run");
            }

            return sortedInputSystemsGlobal;
        }

        public List<int> SortVariableSystems(
            List<SystemInfo> allSystems,
            List<int> variableSystems,
            List<int> globalToLocalIndexMap,
            List<HashSet<int>> systemDepsMap,
            SystemRunPhase runPhaseToSort
        )
        {
            var localDepsList = new List<HashSet<int>>(variableSystems.Count);

            for (
                int systemLocalIndex = 0;
                systemLocalIndex < variableSystems.Count;
                systemLocalIndex++
            )
            {
                var systemGlobalIndex = variableSystems[systemLocalIndex];
                var systemInfo = allSystems[systemGlobalIndex];
                var localDeps = new HashSet<int>();
                localDepsList.Add(localDeps);

                foreach (var otherGlobalIndex in systemDepsMap[systemGlobalIndex])
                {
                    var otherInfo = allSystems[otherGlobalIndex];

                    if (otherInfo.Metadata.RunPhase == runPhaseToSort)
                    {
                        var otherLocalIndex = globalToLocalIndexMap[otherGlobalIndex];
                        Assert.IsEqual(variableSystems[otherLocalIndex], otherGlobalIndex);
                        localDeps.Add(otherLocalIndex);
                    }
                }
            }

            // Now sort to ensure that every system is after all of its dependencies
            // should throw if this is not possible
            var sortedVariableSystemsLocal = TopologicalSorter.Run(
                variableSystems,
                // Order by DeclarationIndex just for extra consistency just in case, though may not matter
                globalIndex =>
                    localDepsList[globalToLocalIndexMap[globalIndex]]
                        .OrderBy(x => allSystems[globalIndex].DeclarationIndex),
                globalIndex => GetSystemAndMetaDataOrderBy(allSystems[globalIndex]),
                globalIndex => allSystems[globalIndex].Metadata.DebugName
            );

            Assert.IsEqual(sortedVariableSystemsLocal.Count, variableSystems.Count);

            var sortedVariableSystemsGlobal = new List<int>(variableSystems.Count);

            for (int i = 0; i < sortedVariableSystemsLocal.Count; i++)
            {
                var localIndex = sortedVariableSystemsLocal[i];
                var globalIndex = variableSystems[localIndex];
                var info = allSystems[globalIndex];
                Assert.That(info.Metadata.RunPhase == runPhaseToSort);
                sortedVariableSystemsGlobal.Add(globalIndex);
            }

            return sortedVariableSystemsGlobal;
        }

        int[] GetSystemAndMetaDataOrderBy(SystemInfo info)
        {
            return new int[]
            {
                info.Metadata.ExecutionPriority ?? 0,
                // Soft preference: FixedUpdateSystem sorts first among variable system peers,
                // so VariableUpdate systems default to running after fixed update
                info.System is FixedUpdateSystem
                    ? -1
                    : 0,
                0,
                info.DeclarationIndex,
            };
        }

        void AddMissingFixedUpdateDependencies(
            List<SystemInfo> allSystems,
            int fixedUpdateIndex,
            List<int> variableSystems,
            List<int> lateVariableSystems,
            List<int> inputSystems,
            List<int> fixedSystems,
            List<HashSet<int>> systemDepsMap
        )
        {
            // Have the FixedUpdateSystem inherit all the constraints of all
            // fixed systems
            // This is necessary because variable systems are sorted separately from fixed systems
            foreach (var globalIndex in variableSystems)
            {
                if (fixedUpdateIndex == globalIndex)
                {
                    continue;
                }

                var systemDeps = systemDepsMap[globalIndex];

                if (
                    systemDeps.Any(x =>
                        allSystems[x].Metadata.RunPhase == SystemRunPhase.Fixed
                        || allSystems[x].Metadata.RunPhase == SystemRunPhase.Input
                    )
                )
                {
                    systemDeps.Add(fixedUpdateIndex);
                }
            }

            var fixedUpdateDeps = systemDepsMap[fixedUpdateIndex];

            foreach (var globalIndex in fixedSystems)
            {
                Assert.That(fixedUpdateIndex != globalIndex);

                var systemDeps = systemDepsMap[globalIndex];

                Assert.That(
                    !systemDeps.Contains(fixedUpdateIndex),
                    "System {} depends on FixedUpdateSystem.  This is not allowed",
                    allSystems[globalIndex].Metadata.DebugName
                );

                foreach (var depIndex in systemDeps)
                {
                    var runPhase = allSystems[depIndex].Metadata.RunPhase;

                    if (runPhase == SystemRunPhase.Variable)
                    {
                        Assert.That(depIndex != fixedUpdateIndex);
                        fixedUpdateDeps.Add(depIndex);
                    }

                    Assert.That(
                        runPhase != SystemRunPhase.LateVariable,
                        "Expected fixed system {} to not depend on any late variable systems (but found {})",
                        allSystems[globalIndex].Metadata.DebugName,
                        allSystems[depIndex].Metadata.DebugName
                    );
                }
            }

            foreach (var globalIndex in inputSystems)
            {
                Assert.That(fixedUpdateIndex != globalIndex);

                var systemDeps = systemDepsMap[globalIndex];

                Assert.That(
                    !systemDeps.Contains(fixedUpdateIndex),
                    "System {} depends on FixedUpdateSystem.  This is not allowed",
                    allSystems[globalIndex].Metadata.DebugName
                );

                foreach (var depIndex in systemDeps)
                {
                    var runPhase = allSystems[depIndex].Metadata.RunPhase;

                    if (runPhase == SystemRunPhase.Variable)
                    {
                        Assert.That(depIndex != fixedUpdateIndex);
                        fixedUpdateDeps.Add(depIndex);
                    }

                    Assert.That(
                        runPhase != SystemRunPhase.LateVariable,
                        "Expected fixed system {} to not depend on any late variable systems (but found {})",
                        allSystems[globalIndex].Metadata.DebugName,
                        allSystems[depIndex].Metadata.DebugName
                    );
                }
            }
        }

        void PrintFullSystemsExecutionOrder(
            List<SystemInfo> allSystems,
            List<int> sortedVariableSystems,
            List<int> sortedInputSystems,
            List<int> sortedLateVariableSystems,
            List<int> sortedFixedSystems,
            int fixedUpdateGlobalIndex
        )
        {
            var message = new StringBuilder();
            message.AppendLine("All sorted systems and their job dependencies: ");

            var completedPhases = new DenseHashSet<SystemRunPhase>();
            SystemRunPhase? currentPhase = null;

            var sortedAllSystems = new List<int>(
                sortedVariableSystems.Count
                    + sortedInputSystems.Count
                    + sortedFixedSystems.Count
                    + sortedLateVariableSystems.Count
            );

            foreach (var i in sortedVariableSystems)
            {
                if (i == fixedUpdateGlobalIndex)
                {
                    foreach (var k in sortedInputSystems)
                    {
                        sortedAllSystems.Add(k);
                    }

                    foreach (var k in sortedFixedSystems)
                    {
                        sortedAllSystems.Add(k);
                    }
                }
                else
                {
                    sortedAllSystems.Add(i);
                }
            }

            foreach (var i in sortedLateVariableSystems)
            {
                sortedAllSystems.Add(i);
            }

            foreach (var globalIndex in sortedAllSystems)
            {
                var info = allSystems[globalIndex];

                if (!currentPhase.HasValue || currentPhase.Value != info.Metadata.RunPhase)
                {
                    if (currentPhase.HasValue)
                    {
                        message.Append("---  ");
                        message.Append(currentPhase.Value.ToString());
                        message.AppendLine(" end ---");

                        completedPhases.Add(currentPhase.Value);
                    }

                    Assert.That(
                        info.Metadata.RunPhase == SystemRunPhase.Variable
                            || !completedPhases.Contains(info.Metadata.RunPhase)
                    );
                    currentPhase = info.Metadata.RunPhase;

                    message.Append("---  ");
                    message.Append(currentPhase.Value.ToString());
                    message.AppendLine(" start ---");

                    completedPhases.Add(currentPhase.Value);
                }

                message.AppendLine(info.Metadata.DebugName);
            }

            _log.Info(message.ToString());
        }

        public LoadInfo LoadSystems(World world, IReadOnlyList<ISystem> systems)
        {
            Assert.That(!_isLocked);
            _isLocked = true;

            var metadatas = _metadataProvider.GetSystemMetadata(world, systems);

            var allSystems = new List<SystemInfo>(metadatas.Count);

            var fixedSystems = new List<int>();
            var inputSystems = new List<int>();
            var variableSystems = new List<int>();
            var lateVariableSystems = new List<int>();

            var globalToLocalIndexMap = new List<int>(metadatas.Count);
            var systemDepsMap = new List<HashSet<int>>(metadatas.Count);

            if (_log.IsDebugEnabled())
            {
                _log.Debug(
                    "{} systems provided to trecs:\n  {l}",
                    metadatas.Count,
                    metadatas.Select(x => x.DebugName).Join("\n  ")
                );
            }

            // Note here that we distinguish between system dependencies and job dependencies
            // System dependencies are just used to determine execution order and do not affect job dependencies
            // After we choose the final order for based on the system dependencies from the metadatas,
            // then we calculate job dependencies by iterating through the sorted systems and calculating
            // whether each previous system has conflicting access to the same components

            int? fixedUpdateGlobalIndex = null;

            for (int i = 0; i < metadatas.Count; i++)
            {
                var metadata = metadatas[i];

                var systemInfo = new SystemInfo(
                    querier: metadata.Accessor,
                    system: metadata.System,
                    metadata: metadata,
                    declarationIndex: i
                );

                if (metadata.System is FixedUpdateSystem fixedUpdateSystem)
                {
                    Assert.That(!fixedUpdateGlobalIndex.HasValue);
                    fixedUpdateGlobalIndex = i;
                }

                var depsSet = metadata.SystemDependencies.ToHashSet();
                Assert.That(
                    !depsSet.Contains(i),
                    "System {} found to depend on itself",
                    metadata.DebugName
                );
                systemDepsMap.Add(depsSet);

                allSystems.Add(systemInfo);

                int localIndex;

                if (metadata.RunPhase == SystemRunPhase.Variable)
                {
                    localIndex = variableSystems.Count;
                    variableSystems.Add(i);
                }
                else if (metadata.RunPhase == SystemRunPhase.LateVariable)
                {
                    localIndex = lateVariableSystems.Count;
                    lateVariableSystems.Add(i);
                }
                else if (metadata.RunPhase == SystemRunPhase.Input)
                {
                    localIndex = inputSystems.Count;
                    inputSystems.Add(i);
                }
                else
                {
                    Assert.That(metadata.RunPhase == SystemRunPhase.Fixed);

                    localIndex = fixedSystems.Count;
                    fixedSystems.Add(i);
                }

                globalToLocalIndexMap.Add(localIndex);
            }

            Assert.That(fixedUpdateGlobalIndex.HasValue);

            AddMissingFixedUpdateDependencies(
                allSystems,
                fixedUpdateGlobalIndex.Value,
                variableSystems: variableSystems,
                lateVariableSystems: lateVariableSystems,
                inputSystems: inputSystems,
                fixedSystems: fixedSystems,
                systemDepsMap
            );

            _log.Debug(
                "Found {} variable systems, {} late variable systems, and {} fixed systems",
                variableSystems.Count,
                lateVariableSystems.Count,
                fixedSystems.Count
            );

            var sortedVariableSystems = SortVariableSystems(
                allSystems,
                variableSystems,
                globalToLocalIndexMap,
                systemDepsMap,
                SystemRunPhase.Variable
            );

            var sortedLateVariableSystems = SortVariableSystems(
                allSystems,
                lateVariableSystems,
                globalToLocalIndexMap,
                systemDepsMap,
                SystemRunPhase.LateVariable
            );

            var sortedInputSystems = SortInputSystems(
                allSystems,
                inputSystems,
                globalToLocalIndexMap,
                systemDepsMap
            );

            var sortedFixedSystems = SortFixedSystems(
                allSystems,
                fixedSystems,
                globalToLocalIndexMap,
                systemDepsMap
            );

            // Job dependencies are computed at runtime by RuntimeJobScheduler.

            if (_log.IsInfoEnabled())
            {
                PrintFullSystemsExecutionOrder(
                    allSystems,
                    sortedVariableSystems,
                    sortedInputSystems,
                    sortedLateVariableSystems,
                    sortedFixedSystems,
                    fixedUpdateGlobalIndex.Value
                );
            }
            return new LoadInfo
            {
                Systems = allSystems,
                SortedVariableSystems = sortedVariableSystems,
                SortedInputSystems = sortedInputSystems,
                SortedLateVariableSystems = sortedLateVariableSystems,
                SortedFixedSystems = sortedFixedSystems,
            };
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public class SystemInfo
        {
            public SystemInfo(
                WorldAccessor querier,
                ISystem system,
                SystemMetadata metadata,
                int declarationIndex
            )
            {
                Querier = querier;
                System = system;
                Metadata = metadata;
                DeclarationIndex = declarationIndex;
            }

            public WorldAccessor Querier { get; }
            public ISystem System { get; }
            public SystemMetadata Metadata { get; }
            public int DeclarationIndex { get; }
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public class LoadInfo
        {
            public List<SystemInfo> Systems;

            // indices into Systems list
            public List<int> SortedVariableSystems;
            public List<int> SortedInputSystems;
            public List<int> SortedLateVariableSystems;
            public List<int> SortedFixedSystems;
        }
    }
}
