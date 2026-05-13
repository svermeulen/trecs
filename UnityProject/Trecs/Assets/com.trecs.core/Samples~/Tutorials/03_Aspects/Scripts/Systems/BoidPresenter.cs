using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.Aspects
{
    [ExecuteIn(SystemPhase.Presentation)]
    public partial class BoidPresenter : ISystem
    {
        readonly RenderableGameObjectManager _goManager;

        public BoidPresenter(RenderableGameObjectManager goManager)
        {
            _goManager = goManager;
        }

        // One way to iterate over aspects is via a method marked with ForEachEntity attribute
        // If this method is also called Execute then this becomes entry point for System
        // We can then specify tags or MatchByComponents
        [ForEachEntity(typeof(SampleTags.Boid))]
        void Execute(in Boid boid)
        {
            var go = _goManager.Resolve(boid.GameObjectId);
            go.transform.position = (Vector3)boid.Position;
            go.GetComponent<Renderer>().material.color = boid.ColorComponent;

            if (math.lengthsq(boid.Velocity) > 0.001f)
            {
                go.transform.rotation = Quaternion.LookRotation((Vector3)boid.Velocity);
            }
        }

        partial struct Boid : IAspect, IRead<GameObjectId, Position, Velocity, ColorComponent> { }
    }
}
