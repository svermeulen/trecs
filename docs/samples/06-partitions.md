# 06 — Partitions

Built-in partition transitions via template partitions. Entities in different partitions are stored in separate groups for cache-friendly, targeted iteration.

**Source:** `Samples/06_Partitions/`

## What It Does

Balls bounce under gravity. When a ball's energy drops below a threshold, it transitions to a "Resting" partition and turns gray. After a rest timer expires, it launches back into the air and returns to "Active".

## Schema

### Tags

```csharp
public struct Ball : ITag { }
public struct Active : ITag { }
public struct Resting : ITag { }
```

### Template with Partitions

```csharp
public partial class BallEntity : ITemplate,
    IHasTags<BallTags.Ball>,
    IHasPartition<BallTags.Active>,
    IHasPartition<BallTags.Resting>
{
    Position Position;
    Velocity Velocity;
    RestTimer RestTimer;
    GameObjectId GameObjectId;
}
```

Each `IHasPartition` declares a valid partition. The entity always has the `Ball` tag plus exactly one partition tag — creating two separate groups in memory.

## Systems

### PhysicsSystem — Active Balls Only

Only processes balls in the Active partition:

```csharp
[ForEachEntity(Tags = new[] { typeof(BallTags.Ball), typeof(BallTags.Active) })]
void Execute(in ActiveBall ball)
{
    var vel = ball.Velocity;
    vel.y += Gravity * World.DeltaTime;
    ball.Position += vel * World.DeltaTime;
    ball.Velocity = vel;

    // Transition to Resting only when energy is low AND on the floor
    if (
        math.lengthsq(ball.Velocity) < RestThreshold * RestThreshold
        && ball.Position.y <= FloorY + 0.01f
    )
    {
        ball.Velocity = float3.zero;
        ball.RestTimer = 2f + World.Rng.Next() * 3f; // rest 2-5 seconds
        ball.MoveTo<BallTags.Ball, BallTags.Resting>(World);
    }
}

partial struct ActiveBall : IAspect, IWrite<Position, Velocity, RestTimer> { }
```

### WakeUpSystem — Resting Balls Only

```csharp
[ExecuteAfter(typeof(PhysicsSystem))]
public partial class WakeUpSystem : ISystem
{
    [ForEachEntity(Tags = new[] { typeof(BallTags.Ball), typeof(BallTags.Resting) })]
    void Execute(in RestingBall ball)
    {
        ball.RestTimer -= World.DeltaTime;

        if (ball.RestTimer <= 0)
        {
            float angle = World.Rng.Next() * 2f * math.PI;
            ball.Velocity = new float3(math.cos(angle) * 2f, LaunchSpeed, math.sin(angle) * 2f);
            ball.MoveTo<BallTags.Ball, BallTags.Active>(World);
        }
    }

    partial struct RestingBall : IAspect, IWrite<Velocity, RestTimer> { }
}
```

### BallRendererSystem — Different Rendering Per Partition

Two `[ForEachEntity]` methods with different tag filters:

```csharp
[Phase(SystemPhase.Presentation)]
public partial class BallRendererSystem : ISystem
{
    [ForEachEntity(Tags = new[] { typeof(BallTags.Ball), typeof(BallTags.Active) })]
    void RenderActive(in ActiveBallView ball)
    {
        // Yellow/red color
    }

    [ForEachEntity(Tags = new[] { typeof(BallTags.Ball), typeof(BallTags.Resting) })]
    void RenderResting(in RestingBallView ball)
    {
        // Gray color
    }

    public void Execute()
    {
        RenderActive();
        RenderResting();
    }
}
```

## Concepts Introduced

- **`IHasPartition`** declares valid partitions on a template
- **`MoveTo<Tag1, Tag2>()`** transitions entities between partitions
- **Partition-filtered iteration** — systems iterate only entities in a specific partition
- **Group separation** — Active and Resting balls are stored in separate contiguous arrays
- **Multiple `[ForEachEntity]` methods** — different queries in the same system, called from an explicit `Execute()`
