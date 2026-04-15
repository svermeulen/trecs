# Getting Started

## Installation

### Via Unity Package Manager (Git URL)

1. Open your Unity project (requires Unity 6000.3.10f1+)
2. Go to **Window > Package Manager**
3. Click **+** > **Add package from git URL...**
4. Enter:
   ```
   https://github.com/svermeulen/trecs.git?path=UnityProject/Trecs/Assets/com.trecs.core
   ```

Alternatively, add directly to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.trecs.core": "https://github.com/svermeulen/trecs.git?path=UnityProject/Trecs/Assets/com.trecs.core"
  }
}
```

### Dependencies

These are installed automatically via UPM:

- Unity Mathematics 1.3.3
- Unity Burst 1.8.28
- Unity Collections 2.6.2

## Your First Project

This walkthrough recreates the HelloEntity sample included with Trecs.

### Step 1: Define Components

Components hold your entity data. They must be unmanaged structs implementing `IEntityComponent`:

```csharp
using Trecs;
using Unity.Mathematics;

public struct CRotation : IEntityComponent
{
    public quaternion Value;

    public static readonly CRotation Default = new()
    {
        Value = quaternion.identity,
    };
}
```

### Step 2: Define Tags and Templates

Tags are marker structs that categorize entities. Templates combine tags with component defaults to define entity archetypes:

```csharp
using Trecs;

public struct Spinner : ITag { }

// partial is required for source generation
public partial class SpinnerEntity : ITemplate, ITags<Spinner>
{
    public CRotation Rotation = CRotation.Default;
}
```

The source generator produces a static `Template` property on each template class that you use when building the world.

### Step 3: Write a System

Systems contain game logic. Use `[ForEachEntity]` to automatically iterate over all entities that have the referenced components:

```csharp
using Trecs;
using Unity.Mathematics;

public partial class SpinnerSystem : ISystem
{
    readonly float _rotationSpeed;

    public SpinnerSystem(float rotationSpeed)
    {
        _rotationSpeed = rotationSpeed;
    }

    [ForEachEntity]
    void Execute(ref CRotation rotation)
    {
        float angle = Ecs.FixedDeltaTime * _rotationSpeed;
        rotation.Value = math.mul(rotation.Value, quaternion.RotateY(angle));
    }
}
```

Key points:
- The class must be `partial` so the source generator can add the iteration code
- `ref` parameters give write access; non-ref parameters are read-only
- `Ecs` is the system's `EcsAccessor`, providing timing, entity operations, and more

### Step 4: Bootstrap the World

Create a MonoBehaviour to build and run the world:

```csharp
using System.Collections.Generic;
using Trecs;
using UnityEngine;

public class Bootstrap : MonoBehaviour
{
    World _world;

    void Awake()
    {
        _world = new WorldBuilder()
            .AddTemplate(SampleTemplates.SpinnerEntity.Template)
            .AddSystem(new SpinnerSystem(2f))
            .Build();

        _world.Initialize();
    }

    void Update()
    {
        _world.Tick();
    }

    void LateUpdate()
    {
        _world.LateTick();
    }

    void OnDestroy()
    {
        _world.Dispose();
    }
}
```

### Step 5: Create Entities

After the world is initialized, create entities using the accessor:

```csharp
// Get the unrestricted accessor (for setup only)
var accessor = _world.UnrestrictedAccessor;

// Schedule an entity with the Spinner tag
var entity = accessor.ScheduleAddEntity<Spinner>();
entity.Set(new CRotation { Value = quaternion.identity });

// Submit all pending entity changes
_world.SubmitEntities();
```

## Next Steps

- [Core Concepts](concepts.md) -- Understand the full ECS model
- [Systems](systems.md) -- Learn about system types, ordering, and source generation
- [Templates](templates.md) -- Advanced template patterns
