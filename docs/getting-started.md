# Getting Started

## Installation

Add Trecs to your Unity project via the Unity Package Manager:

1. Open **Window > Package Manager**
2. Click **+ > Add package from git URL**
3. Enter: `https://github.com/svermeulen/trecs.git?path=UnityProject/Trecs/Assets/com.trecs.core`

To use serialization features (bookmarks, recording/playback, full world state snapshots), also install the optional `com.trecs.serialization` package:

1. Open **Window > Package Manager**
2. Click **+ > Add package from git URL**
3. Enter: `https://github.com/svermeulen/trecs.git?path=UnityProject/Trecs/Assets/com.trecs.serialization`

Requires Unity 6000.3+.

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
    public Rotation Rotation;
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
var worldAccessor = world.CreateAccessor();

// Create an entity
worldAccessor.AddEntity<Spinner>()
    .Set(new Rotation { Value = quaternion.identity });
```

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

