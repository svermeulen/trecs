# 08 — Sets

Dynamic entity subsets with overlapping membership. Unlike partitions (which are mutually exclusive), sets allow an entity to belong to multiple subsets simultaneously.

**Source:** `Samples/08_Sets/`

## What It Does

A grid of particles is affected by two overlapping wave effects — a warm (orange) horizontal wave and a cool (blue) vertical wave. Where the waves overlap, particles turn purple. Particles rise when in the warm wave and scale when in the cool wave.

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

Sets define sparse subsets of entities. Membership is managed at runtime via `SetAdd`/`SetRemove`.

### Registration

Sets must be registered with the world builder:

```csharp
new WorldBuilder()
    .AddSet<SampleSets.WaveX>()
    .AddSet<SampleSets.WaveZ>()
    // ...
```

## Systems

### WaveMembershipSystem

Each frame, determines which particles are inside each wave band and updates set membership:

```csharp
public void Execute()
{
    float waveXCenter = math.sin(World.ElapsedTime * 2f) * _gridSize * 0.6f;
    float waveZCenter = math.cos(World.ElapsedTime * 1.5f) * _gridSize * 0.6f;

    foreach (var p in ParticleView.Query(World).WithTags<SampleTags.Particle>())
    {
        p.WarmIntensity = 0;
        p.CoolIntensity = 0;

        float distX = math.abs(p.Position.x - waveXCenter);
        float distZ = math.abs(p.Position.z - waveZCenter);

        if (distX < waveBandWidth)
            World.SetAdd<SampleSets.WaveX>(p.EntityIndex);
        else
            World.SetRemove<SampleSets.WaveX>(p.EntityIndex);

        if (distZ < waveBandWidth)
            World.SetAdd<SampleSets.WaveZ>(p.EntityIndex);
        else
            World.SetRemove<SampleSets.WaveZ>(p.EntityIndex);
    }
}
```

### WaveXEffectSystem — Iterate Only WaveX Members

```csharp
[ForEachEntity(Set = typeof(SampleSets.WaveX))]
void Execute(in ParticleView p)
{
    float distFromWave = math.abs(p.Position.x - waveCenter);
    p.WarmIntensity = 1f - (distFromWave / waveBandWidth);
}
```

### WaveZEffectSystem — Iterate Only WaveZ Members

Same pattern, scoped to `WaveZ` set.

### ParticleRendererSystem

Composites the final color from warm and cool intensities. A particle in both waves gets both effects blended together.

## Why Sets, Not Partitions?

With partitions, an entity can only be in one partition at a time. To represent "in WaveX", "in WaveZ", and "in both", you'd need four partitions (None, X, Z, XZ) — and that grows as 2^N for N wave effects.

With sets, each wave is independent. A particle can be in zero, one, or both sets simultaneously without any combinatorial explosion.

## Concepts Introduced

- **`IEntitySet`** — defines a sparse entity subset
- **`SetAdd` / `SetRemove`** — deferred membership changes
- **`[ForEachEntity(Set = typeof(...))]`** — iterate only set members
- **Overlapping membership** — entities can be in multiple sets
- **Sets vs Partitions trade-off** — sets avoid combinatorial explosion
