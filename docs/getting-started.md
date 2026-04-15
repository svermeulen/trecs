# Getting Started

## Installation

Add Trecs to your Unity project via the Unity Package Manager:

1. Open **Window > Package Manager**
2. Click **+ > Add package from git URL**
3. Enter: `https://github.com/svermeulen/trecs.git?path=UnityProject/Trecs/Assets/com.trecs.core`

Requires Unity 6000.3+.

## Your First Project

This walkthrough creates a spinning cube — the "Hello World" of Trecs.

### 1. Define a Component

Components are unmanaged structs that hold data:

```csharp
[TypeId(1001)]
[Unwrap]
public partial struct Rotation : IEntityComponent
{
    public quaternion Value;
}
```

### 2. Define a Tag

Tags classify entities into groups:

```csharp
public struct Spinner : ITag { }
```

### 3. Define a Template

Templates declare which components and tags an entity has:

```csharp
public partial class SpinnerEntity : ITemplate, IHasTags<Spinner>
{
    public Rotation Rotation = new(quaternion.identity);
    public GameObjectId GameObjectId;
}
```

### 4. Write a System

Systems contain the logic that operates on entities:

```csharp
public partial class SpinnerSystem : ISystem
{
    readonly float _speed;

    public SpinnerSystem(float speed) { _speed = speed; }

    [ForEachEntity(MatchByComponents = true)]
    void Execute(ref Rotation rotation)
    {
        float angle = World.FixedDeltaTime * _speed;
        rotation.Value = math.mul(rotation.Value, quaternion.RotateY(angle));
    }
}
```

### 5. Build the World

Wire everything together:

```csharp
// Build world
var world = new WorldBuilder()
    .AddTemplate(SpinnerEntity.Template)
    .AddSystem(new SpinnerSystem(speed: 2f))
    .AddSystem(new SpinnerGameObjectUpdater(gameObjectRegistry))
    .BuildAndInitialize();

// Create accessor
var ecs = world.CreateAccessor<MyGame>();

// Create an entity
ecs.AddEntity<Spinner>()
    .Set(new Rotation(quaternion.identity))
    .Set(gameObjectRegistry.Register(cubeGameObject))
    .AssertComplete();
```

### 6. Run the Game Loop

```csharp
void Update()
{
    world.Tick();      // Fixed update systems
    world.LateTick();  // Variable update systems
}

void OnDestroy()
{
    world.Dispose();
}
```

## What's Next

- [Components](core/components.md) — defining and accessing entity data
- [Templates](core/templates.md) — entity blueprints with tags and states
- [Systems](core/systems.md) — writing game logic
- [Queries & Iteration](data-access/queries-and-iteration.md) — finding and processing entities
