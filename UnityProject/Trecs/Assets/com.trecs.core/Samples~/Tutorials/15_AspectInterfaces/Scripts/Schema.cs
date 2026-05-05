namespace Trecs.Samples.AspectInterfaces
{
    public static class SampleTags
    {
        public struct Enemy : ITag { }

        public struct Boss : ITag { }
    }

    [Unwrap]
    public partial struct Armor : IEntityComponent
    {
        public float Value;
    }

    [Unwrap]
    public partial struct MaxHealth : IEntityComponent
    {
        public float Value;
    }

    [Unwrap]
    public partial struct Health : IEntityComponent
    {
        public float Value;
    }

    // Time of the last hit this entity took. Serves two purposes — it's the
    // cooldown clock TryTakeHit gates on, and it's the flash trigger the
    // HitFlashRenderer reads to briefly tint the GameObject white.
    [Unwrap]
    public partial struct HitFlashTime : IEntityComponent
    {
        public float Value;
    }

    // Enemy-only: how fast the enemy moves when charging or fleeing.
    [Unwrap]
    public partial struct ChaseSpeed : IEntityComponent
    {
        public float Value;
    }

    // Enemy-only: absolute ElapsedTime at which the current flee expires.
    // Stamped in EnemyAiSystem when a hit lands, using a random duration
    // sampled per-hit so each retreat lasts a different amount of time.
    [Unwrap]
    public partial struct FleeEndTime : IEntityComponent
    {
        public float Value;
    }

    public enum EnemyMood
    {
        Charging = 0,
        Fleeing = 1,
    }

    // Enemy-only: current behavioral state. Set and cleared by
    // EnemyAiSystem — flipped to Fleeing when a hit lands on the enemy,
    // back to Charging once the stamped FleeEndTime has elapsed.
    [Unwrap]
    public partial struct Mood : IEntityComponent
    {
        public EnemyMood Value;
    }

    // Template defaults are all `default` — SceneInitializer populates
    // every tunable from SampleSettings at spawn time, keeping the knobs
    // in one place (the scene inspector) instead of splitting them
    // between the template and settings.
    public static partial class SampleTemplates
    {
        public partial class EnemyEntity : ITemplate, IHasTags<SampleTags.Enemy>
        {
            Position Position = default;
            Armor Armor = default;
            MaxHealth MaxHealth = default;
            Health Health = default;
            HitFlashTime HitFlashTime = default;
            ChaseSpeed ChaseSpeed = default;
            FleeEndTime FleeEndTime = default;
            Mood Mood = default;
            ColorComponent ColorComponent = default;
            GameObjectId GameObjectId;
        }

        public partial class BossEntity : ITemplate, IHasTags<SampleTags.Boss>
        {
            Position Position = default;
            Armor Armor = default;
            MaxHealth MaxHealth = default;
            Health Health = default;
            HitFlashTime HitFlashTime = default;
            ColorComponent ColorComponent = default;
            GameObjectId GameObjectId;
        }
    }
}
