# Source Generator Reference

Trecs ships a Roslyn source generator that emits iteration code, job wrappers, aspect accessors, template plumbing, and interpolation systems from attributes and interfaces you declare in your own code. You almost never interact with the generator directly — you use the attributes and interfaces listed below, and the generator produces the boilerplate.

This page is a **cross-reference index**, not a tutorial. Each row links to the page where the feature is explained in context.

## Attributes

### Iteration and scheduling

| Attribute | Target | See |
|---|---|---|
| `[ForEachEntity]` / `[ForEachEntity(typeof(Tag), …)]` | iteration methods on `ISystem` / `IJobSystem` / event handlers — auto-routes to component-iteration or aspect-iteration based on the parameter shape | [Systems](../core/systems.md#foreachentity), [Queries & Iteration](../data-access/queries-and-iteration.md), [Entity Events](../entity-management/entity-events.md) |
| `[WrapAsJob]` | methods on `IJobSystem` | [Jobs & Burst](../performance/jobs-and-burst.md) |
| `[FromWorld]` / `[FromWorld(typeof(Tag), …)]` / `[FromWorld(Tags=…)]` | fields on a job struct | [Advanced Job Features](advanced-jobs.md) |
| `[PassThroughArgument]` | method parameters | [Systems](../core/systems.md), [Jobs & Burst](../performance/jobs-and-burst.md) |
| `[SingleEntity]` / `[SingleEntity(typeof(Tag), …)]` | individual method parameters or job-struct fields | [Systems — SingleEntity](../core/systems.md#singleentity) |
| `[GlobalIndex]` | `int` parameters on iteration `Execute` methods | [Advanced Job Features](advanced-jobs.md) — packed 0..N-1 index across all iterated groups |

### System lifecycle

| Attribute | Target | See |
|---|---|---|
| `[ExecuteAfter(typeof(…))]`, `[ExecuteBefore(typeof(…))]` | `ISystem` / `IJobSystem` class | [Systems — Ordering](../core/systems.md) |
| `[ExecutePriority(int)]` | `ISystem` / `IJobSystem` class | [Systems](../core/systems.md) |
| `[ExecuteIn(SystemPhase.Presentation)]` | `ISystem` / `IJobSystem` class | [Systems](../core/systems.md) |
| `[ExecuteIn(SystemPhase.LatePresentation)]` | `ISystem` / `IJobSystem` class | [Systems](../core/systems.md) |
| `[ExecuteIn(SystemPhase.Input)]` | `ISystem` class | [Input System](../core/input-system.md) |

### Components and templates

| Attribute | Target | See |
|---|---|---|
| `[Unwrap]` | single-field component structs | [Components — Unwrap](../core/components.md) |
| `[VariableUpdateOnly]` | component field **or** template class | [Components](../core/components.md) |
| `[Constant]` | component field | [Components](../core/components.md) |
| `[Interpolated]` | template field | [Interpolation](interpolation.md) |
| `[Input(MissingInputBehavior.…)]` | template field on an input component | [Input System](../core/input-system.md) |

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
| `ITagged<…>` | Declares identity tags on a template | [Templates](../core/templates.md), [Tags](../core/tags.md) |
| `IPartitionedBy<…>` | Declares a partition dimension (arity 1 = presence/absence; arity ≥ 2 = explicit variants). Multiple declarations cross-product. | [Templates — Partitions](../core/templates.md#partitions) |
| `IExtends<…>` | Template inheritance | [Templates — Inheritance](../core/templates.md#template-inheritance) |
| `IEntityComponent` | Unmanaged component | [Components](../core/components.md) |
| `ITag` | Zero-cost marker | [Tags](../core/tags.md) |
| `IAspect` + `IRead<…>` / `IWrite<…>` | Bundled component access | [Aspects](../data-access/aspects.md) |
| `IEntitySet` / `IEntitySet<Tag>` | Dynamic entity subset | [Sets](../entity-management/sets.md) |

## `[FromWorld]` field types

The full list of field types `[FromWorld]` can auto-populate is documented at [Advanced Job Features — Supported Field Types](advanced-jobs.md#supported-field-types). At a glance: native component buffers, native component lookups, native set read/write, `NativeWorldAccessor`, and `GroupIndex`.

## Inspecting generated output

When the generator emits code, the output lands in your project's `obj/<Configuration>/<TargetFramework>/generated/` folder. Rider, Visual Studio, and VS Code all expose these files in the Solution Explorer under **Dependencies → Analyzers**. Open the generated file to see exactly what the generator produced for a given `ISystem` / `ITemplate` / aspect.

To enable source-generator timing logs during local debugging, add `SOURCEGEN_TIMING` to the generator's `DefineConstants` in `SourceGen/Trecs.SourceGen/Trecs.SourceGen/Trecs.SourceGen.csproj`. The timings fire to `SourceGenLogger.Log`, which writes to the Unity console.

## Diagnostics

The generator emits structured diagnostics in the `TRECS001`–`TRECS117` range, grouped by area: ForEach iteration, Aspect, Component, Template, AutoSystem, Iteration helper, Hook migration, Job scheduling, FromWorld, WrapAsJob / AutoJob, SingleEntity, GlobalIndex. Two analyzer-emitted codes — `TRECS110` and `TRECS111` — guard against `NativeUniquePtr` copies.

If a diagnostic message isn't self-explanatory, the source of truth is `SourceGen/Trecs.SourceGen/Trecs.SourceGen/DiagnosticDescriptors.cs`. The text usually names the offending attribute or field type — cross-reference with the feature page above for the usage rules.

To rebuild and reinstall the generator DLL after a contributor change:

```bash
cd SourceGen/Trecs.SourceGen && ./build_and_install.sh
```
