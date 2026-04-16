using Unity.Mathematics;

namespace Trecs.Samples.PredatorPrey
{
    /// <summary>
    /// Removes prey entities when their assigned predator gets close enough.
    /// Demonstrates [ForEachEntity] with cross-entity lookups via EntityHandle.
    /// </summary>
    public partial class PredatorChaseSystem : ISystem
    {
        const float CatchRadius = 0.8f;

        [ForEachEntity(Tag = typeof(SampleTags.Predator))]
        void Execute(in Predator predator)
        {
            if (predator.ChosenPrey.IsNull)
            {
                return;
            }

            var prey = new Prey(World, predator.ChosenPrey);

            var delta = prey.Position - predator.Position;
            float dist = math.length(delta);

            if (dist < CatchRadius)
            {
                predator.ChosenPrey = EntityHandle.Null;
                prey.Remove(World);
                return;
            }

            predator.MoveDirection = delta / dist;
        }

        partial struct Predator : IAspect, IRead<Position>, IWrite<ChosenPrey, MoveDirection> { }

        partial struct Prey : IAspect, IRead<Position> { }
    }
}
