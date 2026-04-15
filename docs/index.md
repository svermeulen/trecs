# Trecs

A high-performance Entity Component System framework for Unity.

## Features

- **Group-based storage** — cache-friendly data layout for fast iteration
- **Source generation** — minimal boilerplate with Roslyn-powered code generation
- **Burst & Jobs** — first-class support for Unity's job system and Burst compiler
- **Deterministic simulation** — designed for networking and replay
- **Template system** — composable entity archetypes with tags and states
- **Interpolation** — built-in fixed-to-variable timestep interpolation

## Quick Start

```csharp
// Define a component
[TypeId("Position")]
public struct Position : IEntityComponent
{
    public float X;
    public float Y;
}

// Define a template
[TypeId("Player")]
public partial struct Player : ITemplate,
    ITags<Tag_Player>
{
    public Position Position;
}

// Define a system
public partial class MoveSystem : ISystem
{
    [ForEachEntity]
    void Execute(ref Position pos)
    {
        pos.X += 1f;
    }
}
```

See [Getting Started](getting-started.md) for a complete walkthrough.

## Documentation

<div class="grid cards" markdown>

- :material-rocket-launch: **[Getting Started](getting-started.md)** — Installation and your first project
- :material-book-open-variant: **[Concepts](concepts/index.md)** — Core ECS concepts
- :material-map: **[Guides](guides/aspects-and-source-generation.md)** — In-depth topic guides
- :material-cog: **[Architecture](architecture/system-execution-model.md)** — How Trecs works under the hood

</div>
