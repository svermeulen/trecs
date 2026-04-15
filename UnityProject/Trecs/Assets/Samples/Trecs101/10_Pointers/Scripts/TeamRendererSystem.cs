using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.Pointers
{
    /// <summary>
    /// Variable-update renderer that demonstrates reading both pointer types:
    /// - SharedPtr&lt;TeamConfig&gt;: team color (same for all team members)
    /// - UniquePtr&lt;EntityState&gt;: frame counter drives per-entity scale pulse
    ///
    /// Visual result: all cubes in a team have the same color (shared config),
    /// but each cube pulses at a different phase (unique state).
    /// </summary>
    [VariableUpdate]
    [ExecutesAfter(typeof(TeamOrbitSystem))]
    public partial class TeamRendererSystem : ISystem
    {
        readonly GameObjectRegistry _registry;

        public TeamRendererSystem(GameObjectRegistry registry)
        {
            _registry = registry;
        }

        [ForEachEntity(MatchByComponents = true)]
        void Execute(in Position position, in TeamMember member, in GameObjectId goId)
        {
            var go = _registry.Resolve(goId);
            go.transform.position = (Vector3)position.Value;

            // ─── SharedPtr: team color ──────────────────────────
            var config = member.Config.Get(World);
            go.GetComponent<Renderer>().material.color = config.Color;

            // ─── UniquePtr: per-entity scale pulse ──────────────
            var state = member.State.Get(World);
            float pulse = 0.8f + 0.2f * math.sin(state.FrameCount * 0.1f);
            go.transform.localScale = Vector3.one * pulse * 0.6f;
        }
    }
}
