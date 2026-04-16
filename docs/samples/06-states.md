# 06 — States

Template states for efficient state machines. Entities in different states are stored in separate groups for cache-friendly, targeted iteration.

**Source:** `Samples/06_States/`

## What It Does

Balls bounce under gravity. When a ball's energy drops below a threshold, it transitions to a "Resting" state and turns gray. After a rest timer expires, it launches back into the air and returns to "Active".

## Schema

### Tags

```csharp
public struct Ball : ITag { }
public struct Active : ITag { }
public struct Resting : ITag { }
```

### Template with States

```csharp
public partial class BallEntity : ITemplate,
    IHasTags<BallTags.Ball>,
    IHasState<BallTags.Active>,
    IHasState<BallTags.Resting>
{
    public Position Position;
    public Velocity Velocity;
    public RestTimer RestTimer;
    public GameObjectId GameObjectId;
}
```

Each `IHasState` declares a valid state. The entity always has the `Ball` tag plus exactly one state tag — creating two separate groups in memory.

## Systems

### PhysicsSystem — Active Balls Only

Only processes balls in the Active state:

```csharp
[ForEachEntity(Tags = new[] { typeof(BallTags.Ball), typeof(BallTags.Active) })]
void Execute(in ActiveBall ball)
{
    var vel = ball.Velocity;
    vel.y += Gravity * World.DeltaTime;
    ball.Position += vel * World.DeltaTime;
    ball.Velocity = vel;

    // Transition to Resting when energy is low
    if (math.lengthsq(ball.Velocity) < RestThreshold * RestThreshold)
    {
        ball.Velocity = float3.zero;
        World.MoveTo<BallTags.Ball, BallTags.Resting>(ball.EntityIndex);
    }
}

partial struct ActiveBall : IAspect, IWrite<Position, Velocity, RestTimer> { }
```

### WakeUpSystem — Resting Balls Only

```csharp
[ExecutesAfter(typeof(PhysicsSystem))]
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
            World.MoveTo<BallTags.Ball, BallTags.Active>(ball.EntityIndex);
        }
    }

    partial struct RestingBall : IAspect, IWrite<Velocity, RestTimer> { }
}
```

### BallRendererSystem — Different Rendering Per State

Two `[ForEachEntity]` methods with different tag filters:

```csharp
[VariableUpdate]
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

- **`IHasState`** declares valid states on a template
- **`MoveTo<Tag1, Tag2>()`** transitions entities between states
- **State-filtered iteration** — systems iterate only entities in a specific state
- **Group separation** — Active and Resting balls are stored in separate contiguous arrays
- **Multiple `[ForEachEntity]` methods** — different queries in the same system, called from an explicit `Execute()`
