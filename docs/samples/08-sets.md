# 08 — Sets

Dynamic entity subsets with overlapping membership. Unlike partitions (mutually exclusive), an entity can belong to multiple sets at once.

**Source:** `com.trecs.core/Samples~/Tutorials/08_Sets/`

## What it does

A grid of particles is affected by two overlapping waves — a warm (orange) horizontal wave and a cool (blue) vertical wave. Particles rise in the warm wave, scale in the cool wave, and turn purple where they overlap.

## Schema

### Components

```csharp
[Unwrap]
public partial struct WarmIntensity : IEntityComponent
{
    public float Value;
}

[Unwrap]
public partial struct CoolIntensity : IEntityComponent
{
    public float Value;
}
```

### Sets

```csharp
public struct WaveX : IEntitySet { }
public struct WaveZ : IEntitySet { }
```

Sets define sparse entity subsets. Membership is managed at runtime via `World.Set<T>().Write`, which exposes `.Add()` / `.Remove()` (or `.Clear()` for bulk reset).

### Registration

Register sets with the world builder:

```csharp
var world = new WorldBuilder()
    .AddTemplate(SampleTemplates.ParticleEntity.Template)
    .AddSet<SampleSets.WaveX>()
    .AddSet<SampleSets.WaveZ>()
    .Build();
```

## Systems

### WaveMembershipSystem

Each frame, decides which particles are inside each wave band and updates set membership:

```csharp
public void Execute()
{
    float waveCenterX = math.sin(World.ElapsedTime * _settings.WaveXSpeed) * _gridExtent;
    float waveCenterZ = math.cos(World.ElapsedTime * _settings.WaveZSpeed) * _gridExtent;

    var waveXSet = World.Set<SampleSets.WaveX>().Write;
    waveXSet.Clear();

    var waveZSet = World.Set<SampleSets.WaveZ>().Write;
    waveZSet.Clear();

    foreach (var particle in ParticleView.Query(World).WithTags<SampleTags.Particle>())
    {
        // Reset intensities — downstream effect systems will overwrite
        // for particles that are in their set.
        particle.WarmIntensity = 0;
        particle.CoolIntensity = 0;

        float distX = math.abs(particle.Position.x - waveCenterX);
        float distZ = math.abs(particle.Position.z - waveCenterZ);

        var handle = particle.Handle(World);

        if (distX < _settings.WaveBandWidth)
            waveXSet.Add(handle);

        if (distZ < _settings.WaveBandWidth)
            waveZSet.Add(handle);
    }
}
```

### WaveXEffectSystem — iterate only WaveX members

```csharp
[ForEachEntity(
    typeof(SampleTags.Particle),
    Set = typeof(SampleSets.WaveX)
)]
void Execute(in WaveXView view)
{
    float waveCenterX = math.sin(World.ElapsedTime * _settings.WaveXSpeed) * _gridExtent;
    float dist = math.abs(view.Position.x - waveCenterX);
    view.WarmIntensity = math.saturate(1f - dist / _settings.WaveBandWidth);
}
```

### WaveZEffectSystem — iterate only WaveZ members

Same pattern, scoped to `WaveZ` set.

### ParticlePresenter

Composites final color from warm and cool intensities. A particle in both waves blends both effects.

## Why sets, not partitions?

An entity can only be in one partition at a time. Representing "in WaveX", "in WaveZ", and "in both" needs four partitions (None, X, Z, XZ) — growing as 2^N.

Sets are independent. A particle can be in zero, one, or both sets at once without combinatorial explosion.

The trade-off: sets are sparse and may iterate slower than partitions due to weaker memory locality.

## Concepts introduced

- **`IEntitySet`** — sparse entity subset. See [Sets](../entity-management/sets.md).
- **`World.Set<T>().Write`** with `.Clear()`, `.Add()`, `.Remove()` — direct membership management. See [Structural Changes](../entity-management/structural-changes.md).
- **`[ForEachEntity(Set = typeof(...))]`** — iterate only set members. See [Queries & Iteration](../data-access/queries-and-iteration.md).
- **Overlapping membership** — entities can be in multiple sets at once.
- **Sets vs Partitions** — see [Partitions](06-partitions.md) for the mutually-exclusive alternative, and [Entity Subset Patterns](../guides/entity-subset-patterns.md) for guidance.
