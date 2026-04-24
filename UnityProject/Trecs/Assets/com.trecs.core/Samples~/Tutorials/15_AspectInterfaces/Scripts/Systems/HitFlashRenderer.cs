using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.AspectInterfaces
{
    // Variable-update renderer shared by enemies and the boss. Syncs each
    // GameObject's position to ECS Position, and tints its color — white
    // during the HitFlashDuration window after a hit, otherwise the
    // entity's base color scaled by its current health ratio so low-HP
    // things look faded.
    //
    // Iterates by MatchByComponents rather than by tag, so the same system
    // renders any entity with the shape below regardless of species.
    [VariableUpdate]
    public partial class HitFlashRenderer : ISystem
    {
        readonly GameObjectRegistry _gameObjectRegistry;
        readonly SampleSettings _settings;

        public HitFlashRenderer(GameObjectRegistry gameObjectRegistry, SampleSettings settings)
        {
            _gameObjectRegistry = gameObjectRegistry;
            _settings = settings;
        }

        [ForEachEntity(MatchByComponents = true)]
        void Execute(
            in GameObjectId id,
            in Position position,
            in Health health,
            in MaxHealth maxHealth,
            in HitFlashTime hitFlashTime,
            in ColorComponent baseColor
        )
        {
            var go = _gameObjectRegistry.Resolve(id);
            go.transform.position = (Vector3)position.Value;

            float sinceHit = World.ElapsedTime - hitFlashTime.Value;
            if (sinceHit < _settings.HitFlashDuration)
            {
                go.GetComponent<Renderer>().material.color = Color.white;
            }
            else
            {
                float ratio = math.clamp(health.Value / maxHealth.Value, 0f, 1f);
                go.GetComponent<Renderer>().material.color = baseColor.Value * ratio;
            }
        }
    }
}
