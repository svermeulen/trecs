namespace Trecs
{
    /// <summary>
    /// Presentation-phase system that blends <see cref="InterpolatedPrevious{T}"/> and the current
    /// value of <typeparamref name="T"/> into <see cref="Interpolated{T}"/> each render frame,
    /// using a caller-supplied <see cref="Interpolator"/> delegate.
    /// </summary>
    [Phase(SystemPhase.Presentation)]
    [ExecutePriority(-1000)]
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
            WorldAccessor world
        );
    }
}
