# Source Generator Reference

Trecs ships a Roslyn source generator that emits iteration code, job wrappers, aspect accessors, template plumbing, and interpolation systems from attributes and interfaces you declare in your own code. You almost never interact with the generator directly — you use the attributes and interfaces listed below, and the generator produces the boilerplate.

This page is a **cross-reference index**, not a tutorial. Each row links to the page where the feature is explained in context.

## Attributes

### Iteration and scheduling

| Attribute | Target | See |
|---|---|---|
| `[ForEachEntity]` | methods on `ISystem` / `IJobSystem` / event handlers | [Systems](../core/systems.md#foreachentity), [Queries & Iteration](../data-access/queries-and-iteration.md), [Entity Events](../entity-management/entity-events.md) |
| `[WrapAsJob]` | methods on `IJobSystem` | [Jobs & Burst](../performance/jobs-and-burst.md) |
| `[FromWorld]` / `[FromWorld(Tag=…)]` / `[FromWorld(Tags=…)]` | fields on a job struct | [Advanced Job Features](advanced-jobs.md) |
| `[PassThroughArgument]` | method parameters | [Systems](../core/systems.md), [Jobs & Burst](../performance/jobs-and-burst.md) |
| `[SingleEntity]` | aspect / system-method parameters | [Aspects](../data-access/aspects.md), [Systems](../core/systems.md) |

### System lifecycle

| Attribute | Target | See |
|---|---|---|
| `[ExecuteAfter(typeof(…))]`, `[ExecuteBefore(typeof(…))]` | `ISystem` / `IJobSystem` class | [Systems — Ordering](../core/systems.md) |
| `[ExecutePriority(int)]` | `ISystem` / `IJobSystem` class | [Systems](../core/systems.md) |
| `[Phase(SystemPhase.Presentation)]` | `ISystem` / `IJobSystem` class | [Systems](../core/systems.md) |
| `[Phase(SystemPhase.LatePresentation)]` | `ISystem` / `IJobSystem` class | [Systems](../core/systems.md) |
| `[Phase(SystemPhase.Input)]` | `ISystem` class | [Input System](input-system.md) |

### Components and templates

| Attribute | Target | See |
|---|---|---|
| `[Unwrap]` | single-field component structs | [Components — Unwrap](../core/components.md) |
| `[FixedUpdateOnly]` | component struct | [Components](../core/components.md) |
| `[VariableUpdateOnly]` | component struct | [Components](../core/components.md) |
| `[Constant]` | component field | [Components](../core/components.md) |
| `[Interpolated]` | template field | [Interpolation](interpolation.md) |
| `[Input]` | input component field | [Input System](input-system.md) |

### Interpolation

| Attribute | Target | See |
|---|---|---|
| `[GenerateInterpolatorSystem(name, group)]` | static method | [Interpolation](interpolation.md) |

### Serialization

| Attribute | Target | Purpose |
|---|---|---|
| `[TypeId(int)]` | type | Overrides the auto-generated stable type ID used by the serializer. Use when a type is renamed and existing saves must still load. |
| `[TagId(long)]` / `[SetId(long)]` | tag / set struct | Overrides the stable hash used for group/set identity. Same rationale as `[TypeId]`. |

### Assembly-level

| Attribute | Target | Purpose |
|---|---|---|
| `[assembly: TrecsSourceGenSettings(ComponentPrefix="C")]` | assembly | Strips the given prefix from component type names when generating property/variable names. |

## Interfaces

The generator produces partial-type scaffolding for types that implement these interfaces.

| Interface | Purpose | See |
|---|---|---|
| `ISystem` | Simulation system | [Systems](../core/systems.md) |
| `IJobSystem` | System that schedules jobs | [Jobs & Burst](../performance/jobs-and-burst.md) |
| `ITemplate` | Entity blueprint | [Templates](../core/templates.md) |
| `IHasTags<…>` | Declares identity tags on a template | [Templates](../core/templates.md), [Tags](../core/tags.md) |
| `IHasPartition<…>` | Declares a valid partition on a template | [Templates — Partitions](../core/templates.md#partitions) |
| `IExtends<…>` | Template inheritance | [Templates — Inheritance](../core/templates.md#template-inheritance) |
| `IEntityComponent` | Unmanaged component | [Components](../core/components.md) |
| `ITag` | Zero-cost marker | [Tags](../core/tags.md) |
| `IAspect` + `IRead<…>` / `IWrite<…>` | Bundled component access | [Aspects](../data-access/aspects.md) |
| `IEntitySet` / `IEntitySet<Tag>` | Dynamic entity subset | [Sets](../entity-management/sets.md) |

## `[FromWorld]` field types

The full list of field types `[FromWorld]` can auto-populate is documented at [Advanced Job Features — Supported Field Types](advanced-jobs.md#supported-field-types). At a glance: native component buffers, native component lookups, native set read/write, `NativeWorldAccessor`, and `GroupIndex`.

## When things go wrong

If the generator emits a diagnostic you don't recognize, the message is emitted from `DiagnosticDescriptors.cs` in the generator source. The text usually names the offending attribute or field type — look that up here and cross-reference with the feature page for the usage rules.

To rebuild and reinstall the generator DLL after a contributor change:

```bash
cd SourceGen/Trecs.SourceGen && ./build_and_install.sh
```
