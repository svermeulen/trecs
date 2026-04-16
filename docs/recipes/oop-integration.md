# OOP Integration

Trecs is a pure ECS framework, but Unity games need GameObjects, MonoBehaviours, and other managed objects. This recipe describes a three-layer architecture for bridging ECS and non-ECS code cleanly.

## The Three Layers

```
┌─────────────────────────────────────────┐
│  Layer 1: Non-ECS → ECS (Input)        │
│  MonoBehaviours, services, Unity APIs   │
│  → Queues input into ECS               │
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
│  [VariableUpdate] systems               │
└─────────────────────────────────────────┘
```

## Layer 1: Input Bridge

MonoBehaviours read player input and queue it into the ECS world:

```csharp
public class InputBridge : MonoBehaviour
{
    WorldAccessor _world;
    EntityHandle _globalEntity;

    void Update()
    {
        var dir = new float2(Input.GetAxis("Horizontal"), Input.GetAxis("Vertical"));
        _world.AddInput(_globalEntity, new MoveInput { Direction = dir });
    }
}
```

Use `[InputSystem]` systems to process queued input at the start of each fixed update.

## Layer 2: Pure ECS

Simulation systems contain only ECS logic — no `GameObject`, no `MonoBehaviour`, no `UnityEngine` APIs:

```csharp
public partial class MovementSystem : ISystem
{
    [ForEachEntity(Tags = new[] { typeof(GameTags.Player) })]
    void Execute(in PlayerView player)
    {
        player.Position += player.Velocity * World.DeltaTime;
    }
}
```

This layer is fully deterministic and can be recorded/replayed.

## Layer 3: Output Bridge

Variable-update systems sync ECS state to GameObjects:

```csharp
[VariableUpdate]
public partial class GameObjectSyncSystem : ISystem
{
    readonly GameObjectRegistry _registry;

    [ForEachEntity(MatchByComponents = true)]
    void Execute(in Position pos, in Rotation rot, in GameObjectId id)
    {
        var go = _registry.Resolve(id);
        go.transform.position = (Vector3)pos.Value;
        go.transform.rotation = rot.Value;
    }
}
```

### Spawning and Despawning GameObjects

Use [entity events](../entity-management/entity-events.md) with `[ForEachEntity]` to manage GameObject lifecycle:

```csharp
public partial class EnemyGameObjectManager : IDisposable
{
    readonly GameObjectRegistry _registry;
    readonly DisposeCollection _disposables = new();

    public EnemyGameObjectManager(World world, GameObjectRegistry registry)
    {
        World = world.CreateAccessor();
        _registry = registry;

        World.Events.InGroupsWithTags<GameTags.Enemy>()
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

## Referencing Managed Objects

Use [heap pointer types](../advanced/heap.md) to store managed references in components:

```csharp
public struct AudioSourceRef : IEntityComponent
{
    public SharedPtr<AudioClip> Clip;
}
```

## Why This Separation Matters

- **Determinism** — Layer 2 has no external dependencies, enabling recording and replay
- **Testability** — pure ECS logic can be tested without Unity
- **Portability** — simulation code doesn't depend on specific rendering or input systems
- **Clarity** — each layer has a single responsibility
