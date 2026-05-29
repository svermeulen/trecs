# OOP Integration

Trecs is a pure ECS framework, but Unity games need GameObjects, MonoBehaviours, and other managed objects. A three-layer architecture bridges ECS and non-ECS code cleanly.

## The three layers

```
┌─────────────────────────────────────────┐
│  Layer 1: external state → ECS          │
│  MonoBehaviours, services, Unity APIs   │
│  → Funnel external state via AddInput   │
└─────────────┬───────────────────────────┘
              │ AddInput()
┌─────────────▼───────────────────────────┐
│  Layer 2: Pure ECS (Simulation)         │
│  Systems, components, templates         │
│  No Unity APIs, fully deterministic     │
└─────────────┬───────────────────────────┘
              │ Read components
┌─────────────▼───────────────────────────┐
│  Layer 3: ECS → Non-ECS (Output)        │
│  Sync transforms, spawn GameObjects     │
│  Presentation-phase systems             │
└─────────────────────────────────────────┘
```

## Layer 1: external state → ECS

The simulation must never read non-deterministic external state directly — that breaks record/replay, because a replayed run would re-sample different values. Instead, an `Input`-phase system captures that state each frame and funnels it through [`AddInput`](../core/input-system.md). The framework records whatever is funneled in, so replay feeds the simulation identical values.

This applies to *any* external, non-deterministic source — not just button presses: the cursor's world position, a network message, a sensor reading, or the transform of a GameObject driven by non-ECS code. For example, projecting the cursor onto the ground each frame (which depends on the camera and screen — state the simulation can't see) and handing the player that world point as an aim target:

```csharp
[ExecuteIn(SystemPhase.Input)]
public partial class AimInputSystem : ISystem
{
    readonly Camera _camera;

    public AimInputSystem(Camera camera) => _camera = camera;

    public void Execute()
    {
        if (!World.Query().WithTags<GameTags.Player>().TrySingleHandle(out var player))
            return;

        var ray = _camera.ScreenPointToRay(UnityEngine.Input.mousePosition);
        new Plane(Vector3.up, 0f).Raycast(ray, out var dist);
        player.AddInput(World, new AimInput { Target = (float3)ray.GetPoint(dist) });
    }
}
```

`Input`-phase systems are the only place `AddInput` is allowed; they run just-in-time before each fixed step, so the simulation reads a stable input snapshot.

## Layer 2: pure ECS

Simulation systems contain only ECS logic — no `GameObject`, `MonoBehaviour`, or `UnityEngine` APIs:

```csharp
public partial class MovementSystem : ISystem
{
    [ForEachEntity(typeof(GameTags.Player))]
    void Execute(in PlayerView player)
    {
        player.Position += player.Velocity * World.DeltaTime;
    }
}
```

This layer is fully deterministic and can be recorded / replayed.

## Layer 3: output bridge

Variable-update systems sync ECS state out to GameObjects:

```csharp
[ExecuteIn(SystemPhase.Presentation)]
public partial class GameObjectSyncSystem : ISystem
{
    readonly RenderableGameObjectManager _goManager;

    public GameObjectSyncSystem(RenderableGameObjectManager goManager) =>
        _goManager = goManager;

    [ForEachEntity(MatchByComponents = true)]
    void Execute(in Position pos, in Rotation rot, in GameObjectId id)
    {
        var go = _goManager.Resolve(id);
        go.transform.position = (Vector3)pos.Value;
        go.transform.rotation = rot.Value;
    }
}
```

Note that `RenderableGameObjectManager` and `GameObjectId` aren't part of Trecs — they're sample-side helpers under `Common/`. You can copy them into your project, or roll your own similar way of mapping ECS entities to non ECS entities.

### Spawning and despawning GameObjects

Use [entity events](../entity-management/entity-events.md) with `[ForEachEntity]` to manage GameObject lifecycle reactively:

```csharp
public partial class EnemyGameObjectManager : IDisposable
{
    readonly RenderableGameObjectManager _goManager;
    readonly DisposeCollection _disposables = new();

    public EnemyGameObjectManager(World world, RenderableGameObjectManager goManager)
    {
        World = world.CreateAccessor(AccessorRole.Fixed);
        _goManager = goManager;

        World.Events.EntitiesWithTags<GameTags.Enemy>()
            .OnRemoved(OnEnemyRemoved)
            .AddTo(_disposables);
    }

    WorldAccessor World { get; }

    [ForEachEntity]
    void OnEnemyRemoved(in GameObjectId id)
    {
        var go = _goManager.Resolve(id);
        UnityEngine.Object.Destroy(go);
    }

    public void Dispose() => _disposables.Dispose();
}
```

The `Common/RenderableGameObjectManager.cs` in the samples is exactly this pattern, generalized over `PrefabId` so a single observer can spawn and pool GameObjects for every template that adds the `RenderableGameObject` base — no per-entity wiring required. (`DisposeCollection` and the `.AddTo` helper used above are likewise sample-side, under `Common/`.)

## Why this separation matters

- **Determinism** — Layer 2 has no external dependencies, enabling recording and replay.
- **Testability** — pure ECS logic can be tested without Unity.
- **Portability** — simulation code doesn't depend on specific rendering or input systems.
- **Clarity** — each layer has a single responsibility.
