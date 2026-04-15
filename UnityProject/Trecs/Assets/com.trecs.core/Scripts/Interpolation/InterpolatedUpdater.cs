namespace Trecs
{
    [VariableUpdate]
    [ExecutePriority(-1000)]
    [ExecutesAfter(typeof(FixedUpdateSystem))]
    [AllowMultiple]
    public partial class InterpolatedUpdater<T> : ISystem
        where T : unmanaged, IEntityComponent
    {
        readonly Interpolator _interpolator;

        public InterpolatedUpdater(Interpolator interpolator)
        {
            _interpolator = interpolator;
        }

        public void Execute()
        {
            var percentThroughFixedFrame = InterpolationUtil.CalculatePercentThroughFixedFrame(
                World
            );

            foreach (
                var group in World.WorldInfo.GetGroupsWithComponents<
                    T,
                    InterpolatedPrevious<T>,
                    Interpolated<T>
                >()
            )
            {
                var currents = World.ComponentBuffer<T>(group).Read;
                var previouses = World.ComponentBuffer<InterpolatedPrevious<T>>(group).Read;
                var interpolates = World.ComponentBuffer<Interpolated<T>>(group).Write;
                var count = World.CountEntitiesInGroup(group);

                for (int i = 0; i < count; i++)
                {
                    ref readonly var current = ref currents[i];
                    ref readonly var previous = ref previouses[i];
                    ref var interpolated = ref interpolates[i];

                    _interpolator(
                        previous.Value,
                        current,
                        ref interpolated.Value,
                        percentThroughFixedFrame,
                        World
                    );
                }
            }
        }

        public delegate void Interpolator(
            in T previous,
            in T current,
            ref T interpolated,
            float percentThroughFixedFrame,
            WorldAccessor ecs
        );
    }
}
