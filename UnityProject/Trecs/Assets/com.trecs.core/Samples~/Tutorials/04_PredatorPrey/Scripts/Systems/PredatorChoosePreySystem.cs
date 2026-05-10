using Unity.Mathematics;
using Trecs.Internal;

namespace Trecs.Samples.PredatorPrey
{
    /// <summary>
    /// Each predator chases the nearest prey.
    /// Demonstrates cross-tag aspect queries: reading prey positions
    /// while writing predator velocities.
    /// </summary>
    public partial class PredatorChoosePreySystem : ISystem
    {
        public void Execute()
        {
            foreach (var predator in Predator.Query(World).WithTags<SampleTags.Predator>())
            {
                if (!predator.ChosenPrey.IsNull)
                {
                    continue;
                }

                float nearestDistSq = float.MaxValue;

                Prey chosenPrey = default;
                bool found = false;

                foreach (var prey in Prey.Query(World).WithTags<SampleTags.Prey>())
                {
                    if (!prey.ApproachingPredator.IsNull)
                    {
                        continue;
                    }

                    float distSq = math.distancesq(predator.Position, prey.Position);
                    if (distSq < nearestDistSq)
                    {
                        nearestDistSq = distSq;
                        chosenPrey = prey;
                        found = true;
                    }
                }

                if (found)
                {
                    chosenPrey.ApproachingPredator = predator.EntityIndex.ToHandle(World);
                    predator.ChosenPrey = chosenPrey.EntityIndex.ToHandle(World);
                }
            }
        }

        partial struct Predator : IAspect, IRead<Position>, IWrite<ChosenPrey> { }

        partial struct Prey : IAspect, IRead<Position>, IWrite<ApproachingPredator> { }
    }
}
