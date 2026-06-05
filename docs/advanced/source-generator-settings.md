# Source Generator Settings

The Trecs source generator reads an optional assembly-level attribute, `[TrecsSourceGenSettings]`, that lets you tweak how it generates code for a given assembly. This is a niche feature — most projects never need it — but it's useful when your component naming convention collides with the names the generator picks, or when you want to widen the non-deterministic-iteration analyzer's scope.

The attribute is declared once per assembly, typically in an `AssemblyInfo.cs` file:

```csharp
using Trecs;

[assembly: TrecsSourceGenSettings(ComponentPrefix = "C")]
```

It applies only to the assembly that declares it. Different assemblies in the same project can carry different settings; the generator resolves each component's settings from *its own* declaring assembly, so a `C`-prefixed assembly and an unprefixed one interoperate correctly.

## `ComponentPrefix`

Some codebases prefix every component type name with a marker letter — for example `CPosition`, `CVelocity`, `CHealth` — so components stand out from other types at a glance. By default the source generator names generated members after the full type name, which means an aspect that reads `CPosition` exposes a `aspect.CPosition` property.

Setting `ComponentPrefix` strips that prefix from generated **property and variable names** (it does not rename the type itself):

```csharp
[assembly: TrecsSourceGenSettings(ComponentPrefix = "C")]

// Component is still declared as CPosition:
public partial struct CPosition : IEntityComponent
{
    public float3 Value;
}

// But the aspect property drops the prefix:
partial struct Boid : IAspect, IRead<CVelocity>, IWrite<CPosition> { }

void Move(ref Boid boid)
{
    boid.Position += World.DeltaTime * boid.Velocity;  // "Position", not "CPosition"
}
```

The prefix is stripped only when the next character is uppercase, so a component named `CPosition` becomes `Position`, but a component named `Camera` (where `a` follows `C`) is left untouched. The default is `null`, meaning no stripping.

Stripping also applies through generic and nested component types — the prefix is removed from each leaf type name before the parts are joined. For example `Interpolated<CPos>` generates the name `InterpolatedPos`, and a nested `Outer.CState` generates `OuterState`.

## `GlobalCollectionIterationCheck`

Trecs ships an analyzer (TRECS128 / TRECS129) that flags iteration over collections with non-deterministic enumeration order — `Dictionary`, `HashSet`, `IDictionary`, `IReadOnlyDictionary`, `NativeHashMap`, and `NativeHashSet` — because iterating them inside the deterministic fixed-update simulation can desync replays and netcode.

By default the analyzer only fires inside fixed-update `ISystem` classes, where determinism actually matters. Set `GlobalCollectionIterationCheck = true` to extend the check to *all* code in the assembly, not just fixed-update systems:

```csharp
[assembly: TrecsSourceGenSettings(GlobalCollectionIterationCheck = true)]
```

This is useful if you want the stricter guarantee that no code in the assembly relies on hash-collection iteration order. The default is `false`.
