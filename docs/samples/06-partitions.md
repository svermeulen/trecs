# 06 — Partitions

Built-in partition transitions via template partitions. Entities in different partitions are stored in separate groups for cache-friendly, targeted iteration.

**Source:** `com.trecs.core/Samples~/Tutorials/06_Partitions/`

## What it does

Balls bounce under gravity. When a ball's energy drops below a threshold, it transitions to a "Resting" partition and turns gray. After a rest timer expires, it launches back into the air and returns to "Active".

## Schema

### Tags

```csharp
public struct Ball : ITag { }
public struct Active : ITag { }
```

### Template with partitions

```csharp
public partial class BallEntity : ITemplate,
    ITagged<BallTags.Ball>,
    IPartitionedBy<BallTags.Active>
{
    Position Position;
    Velocity Velocity;
    RestTimer RestTimer;
    GameObjectId GameObjectId;
}
```

`IPartitionedBy<T>` declares a presence/absence partition dimension. Two partitions are emitted: balls with the `Active` tag, and balls without it. The "absent" case has no companion tag — query it with `Without =`.

## Systems

### PhysicsSystem — active balls only

Only processes balls in the Active partition:

```csharp
[ForEachEntity(typeof(BallTags.Ball), typeof(BallTags.Active))]
void Execute(in ActiveBall ball)
{
    var vel = ball.Velocity;
    vel.y += Gravity * World.DeltaTime;
    ball.Position += vel * World.DeltaTime;
    ball.Velocity = vel;

    // Transition to the absent (idle) partition when energy is low AND on the floor
    if (
        math.lengthsq(ball.Velocity) < RestThreshold * RestThreshold
        && ball.Position.y <= FloorY + 0.01f
    )
    {
        ball.Velocity = float3.zero;
        ball.RestTimer = 2f + World.Rng.Next() * 3f; // rest 2-5 seconds
        ball.RemoveTag<BallTags.Active>(World);
    }
}

partial struct ActiveBall : IAspect, IWrite<Position, Velocity, RestTimer> { }
```

### WakeUpSystem — resting balls only

```csharp
[ExecuteAfter(typeof(PhysicsSystem))]
public partial class WakeUpSystem : ISystem
{
    [ForEachEntity(typeof(BallTags.Ball), Without = typeof(BallTags.Active))]
    void Execute(in RestingBall ball)
    {
        ball.RestTimer -= World.DeltaTime;

        if (ball.RestTimer <= 0)
        {
            float angle = World.Rng.Next() * 2f * math.PI;
            ball.Velocity = new float3(math.cos(angle) * 2f, LaunchSpeed, math.sin(angle) * 2f);
            ball.SetTag<BallTags.Active>(World);
        }
    }

    partial struct RestingBall : IAspect, IWrite<Velocity, RestTimer> { }
}
```

### BallRendererSystem — different rendering per partition

Two `[ForEachEntity]` methods with different tag filters:

```csharp
[ExecuteIn(SystemPhase.Presentation)]
public partial class BallRendererSystem : ISystem
{
    [ForEachEntity(typeof(BallTags.Ball), typeof(BallTags.Active))]
    void RenderActive(in ActiveBallView ball)
    {
        // Yellow/red color
    }

    [ForEachEntity(typeof(BallTags.Ball), Without = typeof(BallTags.Active))]
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

## Concepts introduced

- **`IPartitionedBy<T>`** declares a presence/absence partition dimension on a template. See [Templates](../core/templates.md) and [Groups, GroupIndex & TagSets](../advanced/groups-and-tagsets.md).
- **`SetTag<T>()` / `RemoveTag<T>()`** transition entities between partitions by toggling the tag. See [Structural Changes](../entity-management/structural-changes.md).
- **`Without = typeof(T)`** queries the absent partition. See [Queries & Iteration](../data-access/queries-and-iteration.md).
- **Partition-filtered iteration** — systems iterate only entities in a specific partition. See [Queries & Iteration](../data-access/queries-and-iteration.md).
- **Group separation** — Active and Resting balls live in separate contiguous arrays for cache-friendly iteration.
- **Multiple `[ForEachEntity]` methods** — different queries in the same system, called from an explicit `Execute()`. See [Systems](../core/systems.md).
- For dynamic, overlapping membership where partitions don't fit, see [Sets](08-sets.md).
