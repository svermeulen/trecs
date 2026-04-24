// Companion docs: https://svermeulen.github.io/trecs/samples/15-aspect-interfaces/

using System;
using UnityEngine;

namespace Trecs.Samples.AspectInterfaces
{
    [Serializable]
    public class SampleSettings
    {
        // Raw damage applied per hit, before the target's armor is subtracted.
        // Tuned so net damage stays well above EnemyRegenPerSecond * EnemyHitCooldown
        // for every Armor value in the scene — otherwise the tankiest enemy's
        // color barely changes (health oscillates near full).
        public float DamagePerHit = 60f;

        // Minimum seconds between successive hits on each side. Gated
        // per-entity via HitFlashTime inside Combat.TryTakeHit. Split so
        // the boss can soak hits at a different cadence than the enemies
        // it's fighting.
        public float EnemyHitCooldown = 0.5f;
        public float BossHitCooldown = 0.5f;

        // Boss-enemy collision radius. When their centers are closer than
        // this, both sides take a hit (subject to their own cooldowns).
        public float CollisionDistance = 1.2f;

        // Enemy passive-heal rate (per second). Applied every frame so an
        // enemy that fled at low HP recovers some margin before charging
        // back in.
        public float EnemyRegenPerSecond = 15f;

        // Per-hit flee duration is sampled uniformly from this range, so
        // each enemy's retreat lasts a different amount of time and the
        // flock pattern doesn't sync up.
        public float MinFleeDuration = 1.5f;
        public float MaxFleeDuration = 4.5f;

        // Boss move speed. Each frame the boss picks the enemy with the
        // lowest current health and advances toward its position.
        public float BossChaseSpeed = 1f;

        // Per-enemy armor. Same MaxHealth for all, but each enemy soaks a
        // different fraction of DamagePerHit, so their flee cadences and
        // on-screen color (which tints by health ratio) visibly differ.
        // The array length also drives the enemy count.
        public float[] EnemyArmors = { 0f, 8f, 16f, 24f, 32f, 40f };

        public float EnemyMaxHealth = 100f;
        public float EnemyChaseSpeed = 2f;

        public float BossMaxHealth = 1000f;
        public float BossArmor = 25f;

        // Enemy spawn ring around the origin (where the boss starts).
        public float SpawnRingRadius = 4f;
        public float EnemySpawnY = 0.3f;

        // Per-entity render scale (set on the GameObjects, not on any
        // ECS component — there's no UniformScale in this sample).
        public float BossScale = 1.2f;
        public float EnemyScale = 0.6f;

        public Color BossBaseColor = new(0.6f, 0.2f, 0.8f);
        public Color EnemyBaseColor = Color.red;

        // Seconds the HitFlashRenderer tints an entity white after a hit.
        public float HitFlashDuration = 0.15f;
    }
}
