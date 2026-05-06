# OOP Integration

Trecs is a pure ECS framework, but Unity games need GameObjects, MonoBehaviours, and other managed objects. This recipe describes a three-layer architecture for bridging ECS and non-ECS code cleanly.

## The Three Layers

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
│  [Phase(SystemPhase.Presentation)] systems               │
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

Use [`[Phase(SystemPhase.Input)]`](../advanced/input-system.md) systems to process queued input at the start of each fixed update.

## Layer 2: Pure ECS

Simulation systems contain only ECS logic — no `GameObject`, no `MonoBehaviour`, no `UnityEngine` APIs:

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

This layer is fully deterministic and can be recorded/replayed.

## Layer 3: Output Bridge

Variable-update systems sync ECS state to GameObjects:

```csharp
[Phase(SystemPhase.Presentation)]
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
    readonly DisposeCollection _disposables = new(); // sample helper — supply your own IDisposable container

    public EnemyGameObjectManager(World world, GameObjectRegistry registry)
    {
        World = world.CreateAccessor();
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

## Referencing Managed Objects

Use [heap pointer types](../advanced/heap.md) to store managed references in components:

```csharp
public struct AudioSourceRef : IEntityComponent
{
    public SharedPtr<AudioClip> Clip;
}
```

!!! warning "Heap blob types must be serializable"
    If you save/load or record/replay your world, every `T` you allocate on the heap (`SharedPtr<T>`, `UniquePtr<T>`, `NativeSharedPtr<T>`, `NativeUniquePtr<T>`) must have a serializer registered — blobs are written as part of world state using their registered `ISerializer<T>`. Unmanaged `T` is covered by `RegisterBlit<T>`; managed types like Unity `AudioClip` / `Mesh` need a custom `ISerializer<T>` (or `RegisterSkip<T>` if the pointed-to data can be safely reconstructed from elsewhere on load). See [Serialization](../advanced/serialization.md) for details.

## Why This Separation Matters

- **Determinism** — Layer 2 has no external dependencies, enabling recording and replay
- **Testability** — pure ECS logic can be tested without Unity
- **Portability** — simulation code doesn't depend on specific rendering or input systems
- **Clarity** — each layer has a single responsibility
