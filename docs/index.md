# Trecs Documentation

Trecs is a high-performance Entity Component System (ECS) framework for Unity. It uses Roslyn source generators to minimize boilerplate while keeping data in cache-friendly layouts.

## Table of Contents

- [Getting Started](getting-started.md) -- Installation, setup, and your first project
- [Core Concepts](concepts.md) -- World, entities, components, tags, groups, templates, systems, accessors
- [Systems](systems.md) -- Writing systems, source-generated iteration, update phases, system ordering
- [Templates](templates.md) -- Defining entity archetypes with templates
- [Advanced Topics](advanced.md) -- Interpolation, Burst/Jobs, filters, serialization, input queuing, memory management

## Architecture Overview

Trecs is organized around these core abstractions:

```
World
├── WorldBuilder          -- Configures and constructs a World
├── SystemRunner          -- Executes systems in Fixed, Variable, and Late phases
├── EntityQueryer         -- Stores and queries entities by component composition
├── EntitySubmitter       -- Batches entity creation/destruction
└── EcsAccessor (per system)
    ├── Component queries -- Read/write access to component data
    ├── Entity operations -- Create, destroy, move entities
    ├── Events            -- Subscribe to entity lifecycle events
    └── Utilities         -- Timing, RNG, filters
```

**Data flow each frame:**

1. `World.Tick()` runs fixed-update systems (deterministic, at a fixed timestep), then variable-update systems (once per frame)
2. `World.LateTick()` runs late variable-update systems
3. Entity structural changes (add/remove) are batched and submitted between phases

## Project Structure

```
UnityProject/Trecs/Assets/
├── com.trecs.core/Scripts/   -- Core library (19 modules)
│   ├── Accessor/             -- EcsAccessor and permission system
│   ├── Collections/          -- High-performance collections
│   ├── Components/           -- Component storage
│   ├── DataStructures/       -- BitSet, handles, indices
│   ├── Entities/             -- Entity references and factories
│   ├── Filters/              -- Archetype filtering
│   ├── Groups/               -- Tag-based entity grouping
│   ├── Heap/                 -- Memory management (SharedPtr, UniquePtr)
│   ├── Input/                -- Input queuing
│   ├── Interpolation/        -- State interpolation
│   ├── Native/               -- Burst/Jobs integration
│   ├── Query/                -- Entity queries
│   ├── Serialization/        -- ECS state serialization
│   ├── Setup/                -- World and WorldBuilder
│   ├── SourceGen/            -- Source generation attributes and helpers
│   ├── Submission/           -- Entity batching
│   ├── Systems/              -- System interfaces and metadata
│   ├── Templates/            -- Template interfaces
│   └── Util/                 -- Logging and utilities
├── Trecs.Tests/              -- Unit tests (EditMode)
└── Samples/                  -- Example projects
```
