# Trecs

[![Source Generator](https://github.com/svermeulen/trecs/actions/workflows/sourcegen.yml/badge.svg)](https://github.com/svermeulen/trecs/actions/workflows/sourcegen.yml)
[![Unity](https://github.com/svermeulen/trecs/actions/workflows/unity.yml/badge.svg)](https://github.com/svermeulen/trecs/actions/workflows/unity.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
![Unity 6000.3+](https://img.shields.io/badge/Unity-6000.3%2B-black)

A high-performance Entity Component System framework for Unity. Trecs uses Roslyn source generators to minimize boilerplate while keeping data in cache-friendly layouts.

## Features

- **Source-generated systems** -- Write `[ForEachEntity]` methods and let the source generator handle iteration, dependency declaration, and job scheduling
- **Entity templates** -- Declare entity archetypes as C# classes implementing `ITemplate` with typed tags and default component values
- **Deterministic fixed-update loop** -- Separate fixed and variable update phases with independent RNG streams for reproducible simulation
- **Burst/Jobs integration** -- `IJobSystem` interface for high-performance parallel processing with native entity operations
- **Permission-based access** -- Systems declare read/write dependencies via `EcsAccessorBuilder` for safe concurrent access
- **Component interpolation** -- Built-in interpolation between fixed-update states for smooth rendering
- **Entity filters** -- Efficient archetype-based filtering for dynamic entity queries
- **Serialization** -- Save and restore ECS state
- **Input queuing** -- Buffer inputs for network synchronization and replay scenarios

## Requirements

- **Unity** 6000.3.10f1 or later
- **Dependencies** (installed automatically via UPM):
  - Unity Mathematics 1.3.3
  - Unity Burst 1.8.28
  - Unity Collections 2.6.2

## Installation

Add Trecs via the Unity Package Manager using a git URL:

1. Open **Window > Package Manager**
2. Click **+** > **Add package from git URL...**
3. Enter:
   ```
   https://github.com/svermeulen/trecs.git?path=UnityProject/Trecs/Assets/com.trecs.core
   ```

Or add directly to your `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.trecs.core": "https://github.com/svermeulen/trecs.git?path=UnityProject/Trecs/Assets/com.trecs.core"
  }
}
```

## Quick Start

### 1. Define a component

Components are unmanaged structs that hold data:

```csharp
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

### 2. Define a tag and template

Tags categorize entities. Templates combine tags with default component values to define entity archetypes:

```csharp
public struct Spinner : ITag { }

public partial class SpinnerEntity : ITemplate, ITags<Spinner>
{
    public CRotation Rotation = CRotation.Default;
}
```

### 3. Write a system

Systems contain your game logic. Use `[ForEachEntity]` to iterate over matching entities automatically:

```csharp
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

### 4. Build and run a world

```csharp
// Build the world
var world = new WorldBuilder()
    .AddTemplate(SampleTemplates.SpinnerEntity.Template)
    .AddSystem(new SpinnerSystem(2f))
    .Build();

world.Initialize();

// Create entities and tick
world.SubmitEntities();
world.Tick();       // Fixed + variable update
world.LateTick();   // Late variable update

// Clean up
world.Dispose();
```

## Documentation

See the [docs](docs/) folder for detailed documentation:

- [Getting Started](docs/getting-started.md) -- Installation and first project
- [Core Concepts](docs/concepts.md) -- World, entities, components, tags, groups, systems
- [Systems](docs/systems.md) -- Writing systems, source generation, update phases
- [Templates](docs/templates.md) -- Entity templates and archetypes
- [Advanced Topics](docs/advanced.md) -- Interpolation, Burst/Jobs, filters, serialization

## Contributing

Contributions are welcome! See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

## Acknowledgments

Trecs was originally based on [Svelto.ECS](https://github.com/sebas77/Svelto.ECS) by Sebastiano Mandala

## License

Trecs is licensed under the [MIT License](LICENSE).
