namespace Trecs.Samples.Events
{
    /// <summary>
    /// Drives the cube lifecycle:
    /// - Growing cubes increase in scale until they reach full size,
    ///   then transition to the Shrinking state (triggers OnMoved events).
    /// - Shrinking cubes decrease in scale until they disappear,
    ///   then get destroyed (triggers OnRemoved events).
    /// </summary>
    public partial class LifecycleSystem : ISystem
    {
        const float GrowRate = 0.3f;
        const float ShrinkRate = 0.5f;
        const float MaxScale = 1.0f;
        const float MinScale = 0.05f;

        [ForEachEntity(Tags = new[] { typeof(CubeTags.Cube), typeof(CubeTags.Growing) })]
        void Grow(in GrowingCube cube)
        {
            cube.UniformScale += World.DeltaTime * GrowRate;

            if (cube.UniformScale >= MaxScale)
            {
                cube.UniformScale = MaxScale;
                World.MoveTo<CubeTags.Cube, CubeTags.Shrinking>(cube.EntityIndex);
            }
        }

        [ForEachEntity(Tags = new[] { typeof(CubeTags.Cube), typeof(CubeTags.Shrinking) })]
        void Shrink(in ShrinkingCube cube)
        {
            cube.UniformScale -= World.DeltaTime * ShrinkRate;

            if (cube.UniformScale <= MinScale)
            {
                World.RemoveEntity(cube.EntityIndex);
            }
        }

        public void Execute()
        {
            Grow();
            Shrink();
        }

        partial struct GrowingCube : IAspect, IWrite<UniformScale> { }

        partial struct ShrinkingCube : IAspect, IWrite<UniformScale> { }
    }
}
