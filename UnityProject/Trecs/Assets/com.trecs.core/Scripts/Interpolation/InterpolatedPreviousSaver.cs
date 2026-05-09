using System;
using Trecs.Internal;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using EditorBrowsable = System.ComponentModel.EditorBrowsableAttribute;
using EditorBrowsableState = System.ComponentModel.EditorBrowsableState;

namespace Trecs
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class InterpolatedPreviousSaver<T> : IInterpolatedPreviousSaver
        where T : unmanaged, IEntityComponent
    {
        WorldAccessor _world;

        public Type ComponentType
        {
            get { return typeof(T); }
        }

        public void Initialize(World world)
        {
            _world = world.CreateAccessor(AccessorRole.Unrestricted);
        }

        public JobHandle Save()
        {
            var combined = default(JobHandle);
            var scheduler = _world.JobScheduler;
            var currentRid = ResourceId.Component(ComponentTypeId<T>.Value);
            var previousRid = ResourceId.Component(ComponentTypeId<InterpolatedPrevious<T>>.Value);

            foreach (
                var group in _world.WorldInfo.GetGroupsWithComponents<T, InterpolatedPrevious<T>>()
            )
            {
                var (currents, count) = _world.GetBufferReadForJobScheduling<T>(group);
                if (count == 0)
                    continue;
                var (previouses, _) = _world.GetBufferWriteForJobScheduling<
                    InterpolatedPrevious<T>
                >(group);

                var deps = default(JobHandle);
                deps = scheduler.IncludeReadDep(deps, currentRid, group);
                deps = scheduler.IncludeWriteDep(deps, previousRid, group);

                // Manual scheduling: SavePreviousJob is nested inside the generic
                // InterpolatedPreviousSaver<T>, which JobGenerator rejects (TRECS073),
                // so we can't get a generated ScheduleParallel(WorldAccessor) overload
                // here. The IncludeRead/Write/TrackJobRead/Write calls around this
                // block reproduce the same dependency-tracking dance JobGenerator
                // emits in JobGenerator.cs:1162-1213, so suppressing TRECS070 is safe.
#pragma warning disable TRECS070
                var handle = new SavePreviousJob
                {
                    CurrentValues = currents,
                    PreviousValues = previouses,
                }.ScheduleParallel(count, JobsUtil.ChooseBatchSize(count), deps);
#pragma warning restore TRECS070

                scheduler.TrackJobRead(handle, currentRid, group);
                scheduler.TrackJobWrite(handle, previousRid, group);

                combined = JobHandle.CombineDependencies(combined, handle);
            }

            return combined;
        }

        [BurstCompile]
        internal struct SavePreviousJob : IJobFor
        {
            public NativeComponentBufferRead<T> CurrentValues;

            [NativeDisableParallelForRestriction]
            public NativeComponentBufferWrite<InterpolatedPrevious<T>> PreviousValues;

            public readonly void Execute(int i)
            {
                ref readonly var current = ref CurrentValues[i];
                ref var previous = ref PreviousValues[i];
                previous.Value = current;
            }
        }
    }
}
