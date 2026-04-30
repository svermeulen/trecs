using UnityEngine;

namespace Trecs.Samples.Sets
{
    /// <summary>
    /// Syncs ECS visual state to Unity GameObjects.
    ///
    /// Reads the WarmIntensity and CoolIntensity written by the wave effect
    /// systems and composites a final color, height offset, and scale.
    /// Particles in both waves show a purple blend with combined effects.
    /// </summary>
    [ExecuteAfter(typeof(WaveXEffectSystem))]
    [ExecuteAfter(typeof(WaveZEffectSystem))]
    public partial class ParticleRendererSystem : ISystem
    {
        static readonly Color WarmColor = new(1.0f, 0.5f, 0.1f);
        static readonly Color CoolColor = new(0.2f, 0.4f, 1.0f);

        readonly SampleSettings _settings;
        readonly GameObjectRegistry _registry;

        public ParticleRendererSystem(SampleSettings settings, GameObjectRegistry registry)
        {
            _settings = settings;
            _registry = registry;
        }

        [ForEachEntity(Tags = new[] { typeof(SampleTags.Particle) })]
        void Execute(in RenderView view)
        {
            var go = _registry.Resolve(view.GameObjectId);

            float warm = view.WarmIntensity;
            float cool = view.CoolIntensity;

            // Additive color blend: warm → orange, cool → blue, both → purple
            Color color =
                Color.gray + warm * (WarmColor - Color.gray) + cool * (CoolColor - Color.gray);
            go.GetComponent<Renderer>().material.color = color;

            // WaveX lifts particles upward
            var pos = go.transform.position;
            pos.y = view.Position.y + warm * _settings.LiftAmount;
            go.transform.position = pos;

            // WaveZ scales particles up
            float scale = _settings.BaseScale + cool * _settings.ScaleBoost;
            go.transform.localScale = Vector3.one * scale;
        }

        partial struct RenderView
            : IAspect,
                IRead<Position, WarmIntensity, CoolIntensity, GameObjectId> { }
    }
}
