# Getting Started

## Installation

Requires Unity 6000.3+.

### Via OpenUPM (recommended)

With the [openupm-cli](https://openupm.com/):

```bash
openupm add com.trecs.core
# Optional: serialization features (snapshots, recording/playback, save/load)
openupm add com.trecs.serialization
```

Or add manually to `Packages/manifest.json`:

```json
{
  "scopedRegistries": [
    {
      "name": "package.openupm.com",
      "url": "https://package.openupm.com",
      "scopes": ["com.trecs"]
    }
  ],
  "dependencies": {
    "com.trecs.core": "0.1.0",
    "com.trecs.serialization": "0.1.0"
  }
}
```

### Via Git URL

Open **Window > Package Manager**, click **+ > Add package from git URL**, and enter:

```
https://github.com/svermeulen/trecs.git?path=UnityProject/Trecs/Assets/com.trecs.core
```

For the optional serialization package:

```
https://github.com/svermeulen/trecs.git?path=UnityProject/Trecs/Assets/com.trecs.serialization
```

When using git URLs, add `com.trecs.core` before `com.trecs.serialization` (Unity can't resolve versioned dependencies from git URLs).

## Your First Project

This walkthrough creates a spinning cube — the "Hello World" of Trecs.

### 1. Define a Component

Components are unmanaged structs that hold data:

```csharp
public partial struct Rotation : IEntityComponent
{
    public quaternion Value;
}
```

### 2. Define a Tag

Tags classify entities. Systems use tags to filter which entities they operate on:

```csharp
public struct Spinner : ITag { }
```

### 3. Define a Template

Templates are blueprints that declare which components and tags an entity has. The tag on the template is what you use when creating entities and querying them in systems:

```csharp
public partial class SpinnerEntity : ITemplate, IHasTags<Spinner>
{
    Rotation Rotation;
}
```

### 4. Write a System

Systems contain the logic that operates on entities:

```csharp
public partial class SpinnerSystem : ISystem
{
    readonly float _speed;

    public SpinnerSystem(float speed) 
    { 
      _speed = speed; 
    }

    [ForEachEntity(MatchByComponents = true)]
    void Execute(ref Rotation rotation)
    {
        float angle = World.DeltaTime * _speed;
        rotation.Value = math.mul(rotation.Value, quaternion.RotateY(angle));
    }
}
```

### 5. Build the World

Wire everything together:

```csharp
// Build world
var world = new WorldBuilder()
    .AddEntityType(SpinnerEntity.Template)
    .Build();

// Add systems
world.AddSystems(new ISystem[]
{
    new SpinnerSystem(speed: 2f),
    new SpinnerGameObjectUpdater(gameObjectRegistry),
});

// Initialize (allocates groups, initializes systems)
world.Initialize();

// Create accessor
var worldAccessor = world.CreateAccessor(AccessorRole.Fixed);

// Create an entity
worldAccessor.AddEntity<Spinner>()
    .Set(new Rotation { Value = quaternion.identity });
```

`AddEntity<Spinner>()` takes a **tag** — Trecs matches it to `SpinnerEntity.Template` via the template's `IHasTags<Spinner>` declaration. You pass tags, not template types, when adding entities.

### 6. Run the Game Loop

```csharp
void Update()
{
    world.Tick();
}

void LateUpdate()
{
    world.LateTick();
}

void OnDestroy()
{
    world.Dispose();
}
```

