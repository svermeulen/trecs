# 16 — Data-Driven Templates

Entity templates are normally defined as C# classes implementing `ITemplate`,
which the source generator materializes into `Template` instances at compile
time. This sample shows how to build `Template` objects **at runtime** from data
— here a `ScriptableObject` — by calling the public `Template` and
`ComponentDeclaration<T>` constructors directly. The resulting templates are
registered with `WorldBuilder.AddEntityType` exactly like source-generated ones,
and existing component-driven systems iterate over their entities unchanged.

**Why this is useful:**
- Content designers can add new entity archetypes without touching C# source.
- Mod support: load archetype definitions from user-provided JSON / asset bundles.
- Rapid iteration: tweak component composition in the inspector and hit Play.

**Caveat:** the *component types* and *tag types* must still exist at compile
time — the data file picks and mixes from a fixed registry of known types (see
`ArchetypeLoader._componentTypes` and `_tagTypes`). You can't invent brand-new
component layouts at runtime, but you can freely compose any combination of
registered ones.

## Key APIs

- `new Template(debugName, localBaseTemplates, partitions, localComponentDeclarations, localTags)`
  — the `Template` constructor is public (`Trecs` namespace).
- `new ComponentDeclaration<T>(...)` — constructs a component declaration for a
  known `T : unmanaged, IEntityComponent` (`Trecs.Internal` namespace). All
  eight nullable flag arguments can be passed as `null` to accept defaults.
- `TagFactory.CreateTag(Type tagType)` — builds a `Tag` from a `Type` reference
  rather than a generic type argument.
- `TagSet.FromTags(IReadOnlyList<Tag>)` — builds a runtime `TagSet` for spawning.
- `WorldAccessor.AddEntity(TagSet tags)` — spawns an entity from a runtime
  `TagSet` (the non-generic form of `AddEntity<TTag>()`).

## Setup

1. Open the Project window: **Create → Trecs Samples → Archetype Library**. This
   creates an `ArchetypeLibrary` asset.
2. Add a few entries to the `Archetypes` list. Every archetype must include
   `Position`, `Rotation`, and `GameObjectId` so the renderer can sync it. Then
   mix in `OrbitParams` or `BobParams` to opt into the orbit / bob systems.
   Example:

   | Name    | Components                                                    | Tags     |
   |---------|---------------------------------------------------------------|----------|
   | Spinner | Position, Rotation, UniformScale, ColorComponent, GameObjectId | Spinner  |
   | Orbiter | Position, Rotation, UniformScale, ColorComponent, GameObjectId, OrbitParams | Orbiter  |
   | Bobber  | Position, Rotation, UniformScale, ColorComponent, GameObjectId, BobParams   | Bobber   |

3. Create a new scene. Add a Camera. Add a GameObject with **Bootstrap** and
   **DataDrivenCompositionRoot** MonoBehaviours. Drag the DataDrivenCompositionRoot
   into Bootstrap's `CompositionRoot` field. Drag the ArchetypeLibrary asset
   into DataDrivenCompositionRoot's `Library` field.
4. Press Play. You should see one sphere per archetype, each behaving according
   to the components you composed it from.

Documentation: https://svermeulen.github.io/trecs/samples/16-data-driven-templates/
