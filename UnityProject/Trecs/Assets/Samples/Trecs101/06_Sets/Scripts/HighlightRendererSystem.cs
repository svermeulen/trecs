using UnityEngine;

namespace Trecs.Samples.Filters
{
    /// <summary>
    /// Demonstrates iterating a set via Set on [ForEachEntity].
    ///
    /// This system only visits the highlighted particles -- not the entire grid.
    /// The ForEachAspect Set constraint means the loop automatically
    /// iterates only the set's sparse subset.
    /// </summary>
    [ExecutesAfter(typeof(HighlightSystem))]
    public partial class HighlightRendererSystem : ISystem
    {
        readonly GameObjectRegistry _registry;

        public HighlightRendererSystem(GameObjectRegistry registry)
        {
            _registry = registry;
        }

        [ForEachEntity(
            Tags = new[] { typeof(SampleTags.Particle) },
            Set = typeof(SampleSets.HighlightedParticle)
        )]
        void Execute(in HighlightedView view)
        {
            var go = _registry.Resolve(new GameObjectId(view.GameObjectId));
            if (go == null)
            {
                return;
            }

            // Pulse color based on how recently the particle was highlighted
            float age = World.ElapsedTime - view.Lifetime;
            float pulse = Mathf.Clamp01(1f - age * 2f);
            go.GetComponent<Renderer>().material.color = Color.Lerp(
                Color.gray,
                Color.yellow,
                pulse
            );
        }

        partial struct HighlightedView : IAspect, IRead<GameObjectId, Lifetime> { }
    }
}
