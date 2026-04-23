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

    // Records the last time this entity was hit, so HitFlashRenderer can flash
    // the GameObject briefly. Written by Combat.TakeHit.
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

    public enum EnemyMood
    {
        Calm = 0,
        Angry = 1,
        Fleeing = 2,
    }

    // Enemy-only: current behavioral state, driven by AI decisions.
    [Unwrap]
    public partial struct Mood : IEntityComponent
    {
        public EnemyMood Value;
    }

    // Boss-only: 0 when full health, 1 when nearly dead. Derived from Health/MaxHealth
    // by the boss AI each frame; used to gate the boss's self-heal.
    [Unwrap]
    public partial struct EnrageLevel : IEntityComponent
    {
        public float Value;
    }

    public static partial class SampleTemplates
    {
        public partial class EnemyEntity : ITemplate, IHasTags<SampleTags.Enemy>
        {
            public Position Position = default;
            public Armor Armor = new() { Value = 15f };
            public MaxHealth MaxHealth = new() { Value = 100f };
            public Health Health = new() { Value = 100f };
            public HitFlashTime HitFlashTime = default;
            public ChaseSpeed ChaseSpeed = new() { Value = 2f };
            public Mood Mood = default;
            public ColorComponent ColorComponent = default;
            public GameObjectId GameObjectId;
        }

        public partial class BossEntity : ITemplate, IHasTags<SampleTags.Boss>
        {
            public Position Position = default;
            public Armor Armor = new() { Value = 25f };
            public MaxHealth MaxHealth = new() { Value = 200f };
            public Health Health = new() { Value = 200f };
            public HitFlashTime HitFlashTime = default;
            public EnrageLevel EnrageLevel = default;
            public ColorComponent ColorComponent = default;
            public GameObjectId GameObjectId;
        }
    }
}
