# OOP Integration

Trecs is a pure ECS framework, but Unity games need GameObjects, MonoBehaviours, and other managed objects. A three-layer architecture bridges ECS and non-ECS code cleanly.

## The three layers

```
┌─────────────────────────────────────────┐
│  Layer 1: Non-ECS → ECS (Input)         │
│  MonoBehaviours, services, Unity APIs   │
│  → Queues input into ECS                │
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

## Layer 1: input bridge

An input-phase ECS system reads player input and queues it into the world. Keeping the read on the deterministic side means the same input data flows through both live play and replay:

```csharp
[ExecuteIn(SystemPhase.Input)]
public partial class PlayerInputSystem : ISystem
{
    readonly EntityHandle _player;

    public PlayerInputSystem(EntityHandle player) => _player = player;

    public void Execute()
    {
        var dir = new float2(Input.GetAxis("Horizontal"), Input.GetAxis("Vertical"));
        World.AddInput(_player, new MoveInput { Direction = dir });
    }
}
```

`Input`-phase systems are the only place [`AddInput`](../core/input-system.md) is allowed; they run just-in-time before each fixed step so the simulation reads a stable input snapshot.

## Layer 2: pure ECS

Simulation systems contain only ECS logic — no `GameObject`, `MonoBehaviour`, or `UnityEngine` APIs:

```csharp
public partial class MovementSystem : ISystem
{
    [ForEachEntity(typeof(GameTags.Player))]
    void Execute(in PlayerView player)
    {
        player.Position += player.Velocity * World.FixedDeltaTime;
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
    readonly GameObjectRegistry _registry;

    public GameObjectSyncSystem(GameObjectRegistry registry) => _registry = registry;

    [ForEachEntity(MatchByComponents = true)]
    void Execute(in Position pos, in Rotation rot, in GameObjectId id)
    {
        var go = _registry.Resolve(id);
        go.transform.position = (Vector3)pos.Value;
        go.transform.rotation = rot.Value;
    }
}
```

`GameObjectRegistry` and `GameObjectId` are not part of Trecs. See Sample projects for example implementations.

### Spawning and despawning GameObjects

Use [entity events](../entity-management/entity-events.md) with `[ForEachEntity]` to manage GameObject lifecycle:

```csharp
public partial class EnemyGameObjectManager : IDisposable
{
    readonly GameObjectRegistry _registry;
    readonly DisposeCollection _disposables = new();

    public EnemyGameObjectManager(World world, GameObjectRegistry registry)
    {
        World = world.CreateAccessor(AccessorRole.Fixed);
        _registry = registry;

        World.Events.EntitiesWithTags<GameTags.Enemy>()
            .OnRemoved(OnEnemyRemoved)
            .AddTo(_disposables);
    }

    WorldAccessor World { get; }

    [ForEachEntity]
    void OnEnemyRemoved(in GameObjectId id)
    {
        var go = _registry.Resolve(id);
        GameObject.Destroy(go);
        _registry.Unregister(id);
    }

    public void Dispose() => _disposables.Dispose();
}
```

## Why this separation matters

- **Determinism** — Layer 2 has no external dependencies, enabling recording and replay.
- **Testability** — pure ECS logic can be tested without Unity.
- **Portability** — simulation code doesn't depend on specific rendering or input systems.
- **Clarity** — each layer has a single responsibility.
