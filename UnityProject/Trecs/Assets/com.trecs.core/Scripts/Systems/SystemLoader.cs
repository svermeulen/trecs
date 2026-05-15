using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class SystemLoader
    {
        readonly TrecsLog _log;

        readonly ISystemEntryProvider _entryProvider;

        bool _isLocked;

        public SystemLoader(TrecsLog log, ISystemEntryProvider entryProvider)
        {
            _log = log;
            _entryProvider = entryProvider;
        }

        List<int> SortPhaseSystems(
            IReadOnlyList<SystemEntry> allSystems,
            List<int> phaseSystems,
            List<int> globalToLocalIndexMap,
            List<HashSet<int>> systemDepsMap,
            SystemPhase phaseToSort
        )
        {
            var localDepsList = new List<HashSet<int>>(phaseSystems.Count);

            for (
                int systemLocalIndex = 0;
                systemLocalIndex < phaseSystems.Count;
                systemLocalIndex++
            )
            {
                var systemGlobalIndex = phaseSystems[systemLocalIndex];
                var systemInfo = allSystems[systemGlobalIndex];
                var localDeps = new HashSet<int>();
                localDepsList.Add(localDeps);

                TrecsAssert.That(systemInfo.Phase == phaseToSort);

                foreach (var otherGlobalIndex in systemDepsMap[systemGlobalIndex])
                {
                    var otherInfo = allSystems[otherGlobalIndex];

                    if (otherInfo.Phase == phaseToSort)
                    {
                        var otherLocalIndex = globalToLocalIndexMap[otherGlobalIndex];
                        TrecsAssert.IsEqual(phaseSystems[otherLocalIndex], otherGlobalIndex);
                        localDeps.Add(otherLocalIndex);
                    }
                }
            }

            // Now sort to ensure that every system is after all of its dependencies
            // should throw if this is not possible
            var sortedLocal = TopologicalSorter.Run(
                phaseSystems,
                globalIndex =>
                    localDepsList[globalToLocalIndexMap[globalIndex]]
                        .OrderBy(x => allSystems[globalIndex].DeclarationIndex),
                globalIndex => GetSystemEntryOrderBy(allSystems[globalIndex]),
                globalIndex => allSystems[globalIndex].DebugName
            );

            TrecsAssert.IsEqual(sortedLocal.Count, phaseSystems.Count);

            var sortedGlobal = new List<int>(phaseSystems.Count);

            for (int i = 0; i < sortedLocal.Count; i++)
            {
                var localIndex = sortedLocal[i];
                var globalIndex = phaseSystems[localIndex];
                var info = allSystems[globalIndex];
                TrecsAssert.That(info.Phase == phaseToSort);
                sortedGlobal.Add(globalIndex);
            }

            return sortedGlobal;
        }

        int[] GetSystemEntryOrderBy(SystemEntry info)
        {
            return new int[] { info.ExecutionPriority ?? 0, info.DeclarationIndex };
        }

        void PrintFullSystemsExecutionOrder(LoadInfo loadInfo)
        {
            var message = new StringBuilder();
            message.AppendLine("All sorted systems and their execution order: ");

            void AppendPhase(SystemPhase phase, List<int> sorted)
            {
                if (sorted.Count == 0)
                {
                    return;
                }

                message.Append("--- ");
                message.Append(phase.ToString());
                message.AppendLine(" ---");

                foreach (var globalIndex in sorted)
                {
                    message.AppendLine(loadInfo.Systems[globalIndex].DebugName);
                }
            }

            AppendPhase(SystemPhase.EarlyPresentation, loadInfo.SortedEarlyPresentationSystems);
            AppendPhase(SystemPhase.Input, loadInfo.SortedInputSystems);
            AppendPhase(SystemPhase.Fixed, loadInfo.SortedFixedSystems);
            AppendPhase(SystemPhase.Presentation, loadInfo.SortedPresentationSystems);
            AppendPhase(SystemPhase.LatePresentation, loadInfo.SortedLatePresentationSystems);

            _log.Info(message.ToString());
        }

        public LoadInfo LoadSystems(World world, IReadOnlyList<ISystem> systems)
        {
            TrecsAssert.That(!_isLocked);
            _isLocked = true;

            var entries = _entryProvider.GetSystemEntries(world, systems);

            var phaseBuckets = new Dictionary<SystemPhase, List<int>>
            {
                [SystemPhase.Input] = new(),
                [SystemPhase.Fixed] = new(),
                [SystemPhase.EarlyPresentation] = new(),
                [SystemPhase.Presentation] = new(),
                [SystemPhase.LatePresentation] = new(),
            };

            var globalToLocalIndexMap = new List<int>(entries.Count);
            var systemDepsMap = new List<HashSet<int>>(entries.Count);

            if (_log.IsDebugEnabled())
            {
                _log.Debug(
                    "{0} systems provided to trecs:\n  {1}",
                    entries.Count,
                    entries.Select(x => x.DebugName).Join("\n  ")
                );
            }

            // Note here that we distinguish between system dependencies and job dependencies
            // System dependencies are just used to determine execution order and do not affect job dependencies
            // After we choose the final order for based on the system dependencies from the entries,
            // then we calculate job dependencies by iterating through the sorted systems and calculating
            // whether each previous system has conflicting access to the same components

            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                entry.DeclarationIndex = i;

                var depsSet = entry.SystemDependencies.ToHashSet();
                TrecsAssert.That(
                    !depsSet.Contains(i),
                    "System {0} found to depend on itself",
                    entry.DebugName
                );
                systemDepsMap.Add(depsSet);

                var bucket = phaseBuckets[entry.Phase];
                globalToLocalIndexMap.Add(bucket.Count);
                bucket.Add(i);
            }

            _log.Debug(
                "Phase counts — Input: {0}, Fixed: {1}, EarlyPresentation: {2}, Presentation: {3}, LatePresentation: {4}",
                phaseBuckets[SystemPhase.Input].Count,
                phaseBuckets[SystemPhase.Fixed].Count,
                phaseBuckets[SystemPhase.EarlyPresentation].Count,
                phaseBuckets[SystemPhase.Presentation].Count,
                phaseBuckets[SystemPhase.LatePresentation].Count
            );

            List<int> Sort(SystemPhase phase) =>
                SortPhaseSystems(
                    entries,
                    phaseBuckets[phase],
                    globalToLocalIndexMap,
                    systemDepsMap,
                    phase
                );

            var loadInfo = new LoadInfo
            {
                Systems = entries,
                SortedInputSystems = Sort(SystemPhase.Input),
                SortedFixedSystems = Sort(SystemPhase.Fixed),
                SortedEarlyPresentationSystems = Sort(SystemPhase.EarlyPresentation),
                SortedPresentationSystems = Sort(SystemPhase.Presentation),
                SortedLatePresentationSystems = Sort(SystemPhase.LatePresentation),
            };

            // Job dependencies are computed at runtime by RuntimeJobScheduler.

            if (_log.IsInfoEnabled())
            {
                PrintFullSystemsExecutionOrder(loadInfo);
            }

            return loadInfo;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public sealed class LoadInfo
        {
            public IReadOnlyList<SystemEntry> Systems;

            // indices into Systems list, in execution order within each phase
            public List<int> SortedInputSystems;
            public List<int> SortedFixedSystems;
            public List<int> SortedEarlyPresentationSystems;
            public List<int> SortedPresentationSystems;
            public List<int> SortedLatePresentationSystems;
        }
    }
}
