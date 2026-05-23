using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using NUnit.Framework;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Tests for <see cref="ImmutableAnalyzer"/> — TRECS125 (SharedPtr&lt;T&gt;
/// usage-site enforcement) and TRECS126 (content validation of
/// <c>[Trecs.Immutable]</c>-marked classes).
/// </summary>
[TestFixture]
public class Diagnostics_TRECS125_to_126_ImmutableTests
{
    // ───────────── TRECS125 (usage-site) ─────────────

    [Test]
    public void TRECS125_UnmarkedClass_AsSharedPtrTypeArg_Fires()
    {
        const string source = """
            namespace Sample
            {
                public class Plain { }

                public class Holder
                {
                    public Trecs.SharedPtr<Plain> Field;
                }
            }
            """;

        AssertFires(source, "TRECS125");
    }

    [Test]
    public void TRECS125_ImmutableClass_AsSharedPtrTypeArg_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public sealed class Blob
                {
                    public readonly int Value;
                    public Blob(int value) { Value = value; }
                }

                public class Holder
                {
                    public Trecs.SharedPtr<Blob> Field;
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS125");
    }

    [Test]
    public void TRECS125_StringTypeArg_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                public class Holder
                {
                    public Trecs.SharedPtr<string> Field;
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS125");
    }

    [Test]
    public void TRECS125_StaticFactoryAlloc_UnmarkedClass_Fires()
    {
        const string source = """
            namespace Sample
            {
                public class Plain { }

                public static class Helpers
                {
                    public static void Build()
                    {
                        var ptr = Trecs.SharedPtr.Alloc<Plain>(new Plain());
                    }
                }
            }
            """;

        AssertFires(source, "TRECS125");
    }

    [Test]
    public void TRECS125_StaticFactoryAlloc_ImmutableClass_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public sealed class Blob
                {
                    public readonly int Value;
                    public Blob(int value) { Value = value; }
                }

                public static class Helpers
                {
                    public static void Build()
                    {
                        var ptr = Trecs.SharedPtr.Alloc<Blob>(new Blob(0));
                    }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS125");
    }

    [Test]
    public void TRECS125_UnresolvedTypeParameter_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                public static class Helpers<T> where T : class
                {
                    public static Trecs.SharedPtr<T> Field;
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS125");
    }

    [Test]
    public void TRECS125_InheritedImmutableMarker_DoesNotApply()
    {
        // Inherited=false on [Immutable] — derived classes must opt in directly.
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public class Base
                {
                    public readonly int X;
                }

                public sealed class Derived : Base { }

                public class Holder
                {
                    public Trecs.SharedPtr<Derived> Field;
                }
            }
            """;

        AssertFires(source, "TRECS125");
    }

    // ───────────── TRECS125 (interface route) ─────────────

    [Test]
    public void TRECS125_ImmutableInterface_AsSharedPtrTypeArg_DoesNotFire()
    {
        // The "read-only-face" route: an interface marked [Immutable] can be the
        // T in SharedPtr<T>. The underlying concrete may stay mutable; callers
        // holding the SharedPtr see only the read-only surface.
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public interface IReadOnlyBlob
                {
                    int Value { get; }
                }

                public sealed class Blob : IReadOnlyBlob
                {
                    public int Value { get; set; }
                }

                public class Holder
                {
                    public Trecs.SharedPtr<IReadOnlyBlob> Field;
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS125");
    }

    [Test]
    public void TRECS125_UnmarkedInterface_AsSharedPtrTypeArg_Fires()
    {
        // A plain interface without [Immutable] is not enough — we require the
        // opt-in marker to know the author has thought about the contract.
        const string source = """
            namespace Sample
            {
                public interface IPlain
                {
                    int Value { get; }
                }

                public class Holder
                {
                    public Trecs.SharedPtr<IPlain> Field;
                }
            }
            """;

        AssertFires(source, "TRECS125");
    }

    [Test]
    public void TRECS125_StaticFactoryAlloc_ImmutableInterface_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public interface IReadOnlyBlob { int Value { get; } }

                public sealed class Blob : IReadOnlyBlob { public int Value { get; set; } }

                public static class Helpers
                {
                    public static void Build()
                    {
                        var ptr = Trecs.SharedPtr.Alloc<IReadOnlyBlob>(new Blob());
                    }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS125");
    }

    // ───────────── TRECS126 (content validation) ─────────────

    [Test]
    public void TRECS126_NonReadonlyField_Fires()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public sealed class Bad
                {
                    public int Value;  // not readonly
                }
            }
            """;

        AssertFires(source, "TRECS126");
    }

    [Test]
    public void TRECS126_PublicSetter_Fires()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public sealed class Bad
                {
                    public int Value { get; set; }
                }
            }
            """;

        AssertFires(source, "TRECS126");
    }

    [Test]
    public void TRECS126_InitOnlySetter_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public sealed class Good
                {
                    public int Value { get; init; }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS126");
    }

    [Test]
    public void TRECS126_PublicMutableArrayField_Fires()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public sealed class Bad
                {
                    public readonly float[] Heights;
                    public Bad(float[] heights) { Heights = heights; }
                }
            }
            """;

        AssertFires(source, "TRECS126");
    }

    [Test]
    public void TRECS126_PrivateMutableArrayField_DoesNotFire()
    {
        // Canonical pattern: private mutable storage hidden behind a read-only accessor.
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public sealed class Good
                {
                    readonly float[] _heights;
                    public Good(float[] heights) { _heights = heights; }
                    public System.Collections.Generic.IReadOnlyList<float> Heights => _heights;
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS126");
    }

    [Test]
    public void TRECS126_IReadOnlyListOfMutableStruct_DoesNotFire()
    {
        // BCL read-only-view containers return value-type elements by copy, so a
        // mutable element type (think UnityEngine.Color) doesn't enable any path
        // to mutate the blob's underlying storage. Recursion only kicks in for
        // reference-type element args.
        const string source = """
            namespace Sample
            {
                public struct MutColor { public float R, G, B, A; }

                [Trecs.Immutable]
                public sealed class Good
                {
                    readonly MutColor[] _colors;
                    public Good(MutColor[] colors) { _colors = colors; }
                    public System.Collections.Generic.IReadOnlyList<MutColor> Colors => _colors;
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS126");
    }

    [Test]
    public void TRECS126_IReadOnlyListOfMutableClass_Fires()
    {
        // Reference-type elements: the container's indexer returns the shared
        // reference, so mutation through that reference would corrupt the blob.
        const string source = """
            namespace Sample
            {
                public class MutableThing { public int X; }

                [Trecs.Immutable]
                public sealed class Bad
                {
                    public System.Collections.Generic.IReadOnlyList<MutableThing> Things => null;
                }
            }
            """;

        AssertFires(source, "TRECS126");
    }

    [Test]
    public void TRECS126_ImmutableArrayField_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public sealed class Good
                {
                    public readonly System.Collections.Immutable.ImmutableArray<float> Heights;
                    public Good(System.Collections.Immutable.ImmutableArray<float> h) { Heights = h; }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS126");
    }

    [Test]
    public void TRECS126_PublicListField_Fires()
    {
        // List<T> is mutable; even as readonly, the reference points at mutable storage.
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public sealed class Bad
                {
                    public readonly System.Collections.Generic.List<int> Items;
                    public Bad(System.Collections.Generic.List<int> items) { Items = items; }
                }
            }
            """;

        AssertFires(source, "TRECS126");
    }

    [Test]
    public void TRECS126_NonImmutableBase_Fires()
    {
        const string source = """
            namespace Sample
            {
                public class Base { public readonly int X; }

                [Trecs.Immutable]
                public sealed class Derived : Base { }
            }
            """;

        AssertFires(source, "TRECS126");
    }

    [Test]
    public void TRECS126_ImmutableBase_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public class Base { public readonly int X; }

                [Trecs.Immutable]
                public sealed class Derived : Base { }
            }
            """;

        AssertDoesNotFire(source, "TRECS126");
    }

    [Test]
    public void TRECS126_FieldOfImmutableClass_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public sealed class Inner
                {
                    public readonly int X;
                }

                [Trecs.Immutable]
                public sealed class Outer
                {
                    public readonly Inner Nested;
                    public Outer(Inner inner) { Nested = inner; }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS126");
    }

    [Test]
    public void TRECS126_FieldOfPlainClass_Fires()
    {
        const string source = """
            namespace Sample
            {
                public sealed class Plain { public readonly int X; }

                [Trecs.Immutable]
                public sealed class Bad
                {
                    public readonly Plain Inner;
                    public Bad(Plain inner) { Inner = inner; }
                }
            }
            """;

        AssertFires(source, "TRECS126");
    }

    [Test]
    public void TRECS126_FieldOfReadonlyStruct_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                public readonly struct Snap { public readonly int X; }

                [Trecs.Immutable]
                public sealed class Good
                {
                    public readonly Snap Snap;
                    public Good(Snap s) { Snap = s; }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS126");
    }

    [Test]
    public void TRECS126_FieldOfPureValueNonReadonlyStruct_DoesNotFire()
    {
        // A pure-value non-readonly struct (no reference-type fields) is
        // accepted as a safe field type. Caller can't reach mutable shared
        // state through `instance.M`: the field is `readonly` so CS1648
        // forbids `instance.M.X = 5`, and reads return a fresh copy. This
        // is the rule that admits Unity.Mathematics.float3 / quaternion /
        // int3 etc. as field types without a hand-maintained allowlist.
        const string source = """
            namespace Sample
            {
                public struct Mutable { public int X; }

                [Trecs.Immutable]
                public sealed class Good
                {
                    public readonly Mutable M;
                    public Good(Mutable m) { M = m; }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS126");
    }

    [Test]
    public void TRECS126_FieldLikeEvent_Fires()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public sealed class Bad
                {
                    public event System.Action OnChange;
                }
            }
            """;

        AssertFires(source, "TRECS126");
    }

    [Test]
    public void TRECS126_StaticMutableField_DoesNotFire()
    {
        // Static fields are not part of instance immutability.
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public sealed class Good
                {
                    public static int Counter;
                    public readonly int Value;
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS126");
    }

    [Test]
    public void TRECS126_UnmarkedClass_NotChecked()
    {
        // The content validation only fires on [Immutable]-marked classes.
        const string source = """
            namespace Sample
            {
                public class Plain
                {
                    public int Mutable;
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS126");
    }

    [Test]
    public void TRECS126_SpanProperty_Fires()
    {
        // Span<T> is a `readonly ref struct` but its indexer returns `ref T`,
        // so a Span field/property on an [Immutable] type would still let
        // callers mutate elements. Must be rejected even though the readonly-
        // struct gate would otherwise let it through.
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public sealed class Bad
                {
                    public System.Span<float> S => default;
                }
            }
            """;

        AssertFires(source, "TRECS126");
    }

    [Test]
    public void TRECS126_ReadOnlySpanProperty_DoesNotFire()
    {
        // ReadOnlySpan<T> is the safe counterpart — readonly ref struct that
        // only exposes `ref readonly T`.
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public sealed class Good
                {
                    readonly float[] _heights;
                    public Good(float[] h) { _heights = h; }
                    public System.ReadOnlySpan<float> Heights => _heights;
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS126");
    }

    [Test]
    public void TRECS126_ReadonlyStructOfMutableClass_Fires()
    {
        // Consistency with the BCL-allowlist rule: readonly struct generics
        // that hold reference-type elements expose the inner reference, so
        // their type args must also be immutable.
        const string source = """
            namespace Sample
            {
                public class MutableThing { public int X; }

                public readonly struct Box<T> { public readonly T Value; public Box(T v) { Value = v; } }

                [Trecs.Immutable]
                public sealed class Bad
                {
                    public readonly Box<MutableThing> Wrapped;
                    public Bad(Box<MutableThing> b) { Wrapped = b; }
                }
            }
            """;

        AssertFires(source, "TRECS126");
    }

    [Test]
    public void TRECS126_ReadonlyStructOfImmutableClass_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public sealed class Inner { public readonly int X; }

                public readonly struct Box<T> { public readonly T Value; public Box(T v) { Value = v; } }

                [Trecs.Immutable]
                public sealed class Good
                {
                    public readonly Box<Inner> Wrapped;
                    public Good(Box<Inner> b) { Wrapped = b; }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS126");
    }

    [Test]
    public void TRECS126_ReadonlyStructOfValueType_DoesNotFire()
    {
        // Value-type args don't trigger the reference-recurse rule; the
        // container returns a copy per access.
        const string source = """
            namespace Sample
            {
                public struct MutableStruct { public int X; }

                public readonly struct Box<T> { public readonly T Value; public Box(T v) { Value = v; } }

                [Trecs.Immutable]
                public sealed class Good
                {
                    public readonly Box<MutableStruct> Wrapped;
                    public Good(Box<MutableStruct> b) { Wrapped = b; }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS126");
    }

    [Test]
    public void TRECS126_ImmutableRecord_DoesNotFire()
    {
        // Records emit a synthetic `EqualityContract { get; }` property of
        // type System.Type, which is a non-[Immutable] class. The analyzer
        // must skip implicitly-declared members so the record is accepted.
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public sealed record Foo(int X, string Name);
            }
            """;

        AssertDoesNotFire(source, "TRECS126");
    }

    // ───────────── TRECS126 (interface content validation) ─────────────

    [Test]
    public void TRECS126_GetOnlyInterface_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public interface IReadOnlyFoo
                {
                    int X { get; }
                    string Name { get; }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS126");
    }

    [Test]
    public void TRECS126_InterfaceWithSetter_Fires()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public interface IBad
                {
                    int X { get; set; }
                }
            }
            """;

        AssertFires(source, "TRECS126");
    }

    [Test]
    public void TRECS126_InterfaceWithInitSetter_DoesNotFire()
    {
        // init-only setters are allowed on interfaces (C# 9 records / DTOs).
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public interface IGood
                {
                    int X { get; init; }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS126");
    }

    [Test]
    public void TRECS126_InterfaceWithEvent_Fires()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public interface IBad
                {
                    event System.Action OnChanged;
                }
            }
            """;

        AssertFires(source, "TRECS126");
    }

    [Test]
    public void TRECS126_InterfacePropertyOfMutableClass_Fires()
    {
        // The interface declares a getter returning a non-[Immutable] class —
        // callers can reach mutable state through the interface, defeating the
        // read-only-face guarantee.
        const string source = """
            namespace Sample
            {
                public sealed class Mutable { public int X; }

                [Trecs.Immutable]
                public interface IBad
                {
                    Mutable Inner { get; }
                }
            }
            """;

        AssertFires(source, "TRECS126");
    }

    [Test]
    public void TRECS126_InterfacePropertyOfImmutableInterface_DoesNotFire()
    {
        // Property of another [Immutable] interface should pass the safe-type
        // walker recursively.
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public interface IReadOnlyInner { int X { get; } }

                [Trecs.Immutable]
                public interface IReadOnlyOuter { IReadOnlyInner Inner { get; } }
            }
            """;

        AssertDoesNotFire(source, "TRECS126");
    }

    [Test]
    public void TRECS126_InterfaceUsedAsFieldType_DoesNotFire()
    {
        // An [Immutable] class can hold a public readonly field of an [Immutable]
        // interface — the safe-type walker should accept it the same way it
        // accepts an [Immutable] class field.
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public interface IReadOnlyInner { int X { get; } }

                [Trecs.Immutable]
                public sealed class Outer
                {
                    public readonly IReadOnlyInner Inner;
                    public Outer(IReadOnlyInner inner) { Inner = inner; }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS126");
    }

    [Test]
    public void TRECS126_InterfaceMethodsAreNotCheckedAsTRECS126()
    {
        // Methods on [Immutable] interfaces don't contribute to TRECS126 (the
        // "violates the immutability contract" rollup) — they're audited
        // separately by TRECS127 with a Warning severity and a per-method
        // squiggle. This test pins that separation: TRECS126 stays silent on
        // method declarations even when their return type would fail the
        // safe-type walker.
        const string source = """
            namespace Sample
            {
                public sealed class Mutable { public int X; }

                [Trecs.Immutable]
                public interface IFoo
                {
                    int Get();
                    [Trecs.AllowMutableReturn]
                    Mutable LookUp(int id);
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS126");
    }

    // ───────────── TRECS126 (pure-value struct admission) ─────────────

    [Test]
    public void TRECS126_PureValueStructField_DoesNotFire()
    {
        // A non-readonly struct whose fields are all value types (recursively)
        // is safe as a field type on an [Immutable] class: `public readonly
        // Pod Position;` returns a copy on every access AND C# forbids
        // `instance.Position.X = 5` (CS1648 on a readonly value-typed field).
        // This is the rule that admits Unity.Mathematics.float3 / quaternion /
        // int3 without a hand-maintained namespace allowlist.
        const string source = """
            namespace Sample
            {
                public struct Pod
                {
                    public float X;
                    public float Y;
                    public float Z;
                }

                [Trecs.Immutable]
                public sealed class Good
                {
                    public readonly Pod Position;
                    public Good(Pod p) { Position = p; }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS126");
    }

    [Test]
    public void TRECS126_StructWithReferenceField_Fires()
    {
        // A non-readonly struct that contains a reference-typed field is NOT
        // safe — a caller could navigate to the heap object and mutate it.
        const string source = """
            namespace Sample
            {
                public sealed class MutableInner { public int X; }

                public struct Holder
                {
                    public MutableInner Ref;
                }

                [Trecs.Immutable]
                public sealed class Bad
                {
                    public readonly Holder Field;
                    public Bad(Holder h) { Field = h; }
                }
            }
            """;

        AssertFires(source, "TRECS126");
    }

    [Test]
    public void TRECS126_NestedPureValueStruct_DoesNotFire()
    {
        // Recursion: a pure-value struct containing another pure-value struct
        // is still safe.
        const string source = """
            namespace Sample
            {
                public struct InnerPod { public float V; }
                public struct OuterPod { public InnerPod Inner; public int Tag; }

                [Trecs.Immutable]
                public sealed class Good
                {
                    public readonly OuterPod Value;
                    public Good(OuterPod v) { Value = v; }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS126");
    }

    // ───────── TRECS126 (Unity native read-only views — allowlist) ─────────

    [Test]
    public void TRECS126_NativeArrayReadOnlyOfValueType_DoesNotFire()
    {
        // The canonical motivating example: a read-only view over a NativeArray<T>
        // of pure-value-type elements is the Unity analog of IReadOnlyList<int>.
        // The indexer returns a copy, no mutation verbs are exposed.
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public sealed class Good
                {
                    public readonly Unity.Collections.NativeArray<int>.ReadOnly Values;
                    public Good(Unity.Collections.NativeArray<int>.ReadOnly v) { Values = v; }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS126");
    }

    [Test]
    public void TRECS126_NativeArrayReadOnlyProperty_DoesNotFire()
    {
        // Mirrors the orca IReadOnlyCaveNavMesh shape that motivated this task —
        // expose the read-only view as a property on an [Immutable] interface.
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public interface IReadOnlyMesh
                {
                    Unity.Collections.NativeArray<int>.ReadOnly Triangles { get; }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS126");
    }

    [Test]
    public void TRECS126_NativeHashMapReadOnly_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public sealed class Good
                {
                    public readonly Unity.Collections.NativeHashMap<int, float>.ReadOnly Map;
                    public Good(Unity.Collections.NativeHashMap<int, float>.ReadOnly m) { Map = m; }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS126");
    }

    [Test]
    public void TRECS126_NativeHashSetReadOnly_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public sealed class Good
                {
                    public readonly Unity.Collections.NativeHashSet<int>.ReadOnly Set;
                    public Good(Unity.Collections.NativeHashSet<int>.ReadOnly s) { Set = s; }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS126");
    }

    [Test]
    public void TRECS126_NativeParallelHashMapReadOnly_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public sealed class Good
                {
                    public readonly Unity.Collections.NativeParallelHashMap<int, int>.ReadOnly Map;
                    public Good(Unity.Collections.NativeParallelHashMap<int, int>.ReadOnly m) { Map = m; }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS126");
    }

    [Test]
    public void TRECS126_NativeParallelMultiHashMapReadOnly_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public sealed class Good
                {
                    public readonly Unity.Collections.NativeParallelMultiHashMap<int, int>.ReadOnly Map;
                    public Good(Unity.Collections.NativeParallelMultiHashMap<int, int>.ReadOnly m) { Map = m; }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS126");
    }

    [Test]
    public void TRECS126_WritableNativeArrayField_Fires()
    {
        // The *writable* NativeArray<T> exposes `this[int] { get; set; }`, so a
        // `readonly NativeArray<int> Values;` field on an [Immutable] class
        // would let callers do `instance.Values[0] = 5` — the indexer setter
        // writes through the pointer the array holds, not to the array value
        // itself, so the readonly-field guarantee (CS1648) doesn't catch it.
        // The struct-mutation-surface check rejects this kind: NativeArray<T>
        // is decorated with [NativeContainer] AND exposes a writable indexer
        // setter, so any one of options (a)/(b) in ComputeIsSafe would fire —
        // they layer for defense in depth.
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public sealed class Bad
                {
                    public readonly Unity.Collections.NativeArray<int> Values;
                    public Bad(Unity.Collections.NativeArray<int> v) { Values = v; }
                }
            }
            """;

        AssertFires(source, "TRECS126");
    }

    [Test]
    public void TRECS126_NativeSliceField_Fires()
    {
        // NativeSlice<T> is intentionally *not* in the external-library
        // allowlist (its indexer has a setter). The struct-mutation-surface
        // check catches it via both the [NativeContainer] attribute and the
        // writable indexer setter.
        const string source = """
            namespace Sample
            {
                [Trecs.Immutable]
                public sealed class Bad
                {
                    public readonly Unity.Collections.NativeSlice<int> Slice;
                    public Bad(Unity.Collections.NativeSlice<int> s) { Slice = s; }
                }
            }
            """;

        AssertFires(source, "TRECS126");
    }

    // ───── TRECS126 (struct-mutation-surface — option-by-option targeted tests) ─────

    [Test]
    public void TRECS126_StructWithWritableIndexerSetter_Fires()
    {
        // Direct test of the writable-indexer-setter rejection independent of
        // [NativeContainer] / pointer fields: a hand-rolled struct that holds
        // no reference fields and no pointers but exposes a settable indexer
        // is still mutable through `instance.Field[0] = 5` and must be
        // rejected.
        const string source = """
            namespace Sample
            {
                public struct WritableIndexer
                {
                    public int this[int i] { get { return 0; } set { } }
                }

                [Trecs.Immutable]
                public sealed class Bad
                {
                    public readonly WritableIndexer Field;
                    public Bad(WritableIndexer f) { Field = f; }
                }
            }
            """;

        AssertFires(source, "TRECS126");
    }

    [Test]
    public void TRECS126_StructWithInitOnlyIndexer_DoesNotFire()
    {
        // init-only setters can only run during object initialization, so the
        // post-construction mutability concern doesn't apply. Mirrors the
        // existing init-only carve-out for property setters.
        const string source = """
            namespace Sample
            {
                public struct InitIndexer
                {
                    public int this[int i] { get { return 0; } init { } }
                }

                [Trecs.Immutable]
                public sealed class Good
                {
                    public readonly InitIndexer Field;
                    public Good(InitIndexer f) { Field = f; }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS126");
    }

    [Test]
    public void TRECS126_StructWithGetOnlyIndexer_DoesNotFire()
    {
        // A read-only indexer (no setter) is fine; the struct is otherwise a
        // pure-value type and the indexer can only compute / read.
        const string source = """
            namespace Sample
            {
                public struct GetIndexer
                {
                    public int this[int i] => 0;
                }

                [Trecs.Immutable]
                public sealed class Good
                {
                    public readonly GetIndexer Field;
                    public Good(GetIndexer f) { Field = f; }
                }
            }
            """;

        AssertDoesNotFire(source, "TRECS126");
    }

    [Test]
    public void TRECS126_StructWithPointerField_Fires()
    {
        // A struct that holds an unsafe pointer can mutate the pointee through
        // any method, even if it has no indexer setter and isn't decorated
        // [NativeContainer]. Pointer types are an analyzer dead-end — we can't
        // prove what they point at — so we reject the struct outright.
        const string source = """
            namespace Sample
            {
                public unsafe struct PointerHolder
                {
                    public int* _ptr;
                }

                [Trecs.Immutable]
                public sealed class Bad
                {
                    public readonly PointerHolder Field;
                    public Bad(PointerHolder f) { Field = f; }
                }
            }
            """;

        AssertFires(source, "TRECS126");
    }

    [Test]
    public void TRECS126_StructWithFunctionPointerField_Fires()
    {
        // Function-pointer fields fall under the same "unanalyzable" reasoning
        // as raw pointer fields. The function pointer itself isn't mutable,
        // but treating function-pointer-bearing structs uniformly with raw-
        // pointer-bearing ones keeps the rule simple.
        const string source = """
            namespace Sample
            {
                public unsafe struct FuncPtrHolder
                {
                    public delegate*<int, int> _fn;
                }

                [Trecs.Immutable]
                public sealed class Bad
                {
                    public readonly FuncPtrHolder Field;
                    public Bad(FuncPtrHolder f) { Field = f; }
                }
            }
            """;

        AssertFires(source, "TRECS126");
    }

    [Test]
    public void TRECS126_StructWithNativeContainerAttribute_Fires()
    {
        // Even without an indexer or a pointer field, a struct decorated with
        // Unity's [NativeContainer] is by-convention a wrapper over native
        // memory with mutation verbs through pointer storage — Roslyn can't
        // see the actual pointer (it might live in a referenced assembly).
        // Reject by attribute as a defense-in-depth backstop.
        const string source = """
            namespace Sample
            {
                [Unity.Collections.LowLevel.Unsafe.NativeContainer]
                public struct OpaqueNativeContainer { }

                [Trecs.Immutable]
                public sealed class Bad
                {
                    public readonly OpaqueNativeContainer Field;
                    public Bad(OpaqueNativeContainer f) { Field = f; }
                }
            }
            """;

        AssertFires(source, "TRECS126");
    }

    [Test]
    public void TRECS126_ReadonlyStructWithWritableIndexer_Fires()
    {
        // The struct-mutation-surface check applies to readonly structs too,
        // not just non-readonly ones — a `readonly struct` with a settable
        // indexer is unusual but not impossible, and the same mutation-via-
        // pointer hazard applies. Mirrors the Span<T> blocklist that exists
        // for the same reason (Span is `readonly ref struct` with a
        // ref-returning indexer that lets callers mutate elements).
        const string source = """
            namespace Sample
            {
                public readonly struct ReadonlyWritableIndexer
                {
                    public int this[int i] { get { return 0; } set { } }
                }

                [Trecs.Immutable]
                public sealed class Bad
                {
                    public readonly ReadonlyWritableIndexer Field;
                    public Bad(ReadonlyWritableIndexer f) { Field = f; }
                }
            }
            """;

        AssertFires(source, "TRECS126");
    }

    // ───────────── helpers ─────────────

    static void AssertFires(string source, string expectedId)
    {
        var diagnostics = GeneratorTestHarness.RunAnalyzers(
            new DiagnosticAnalyzer[] { new ImmutableAnalyzer() },
            source
        );
        var diag = diagnostics.FirstOrDefault(d => d.Id == expectedId);
        Assert.That(diag, Is.Not.Null, $"Expected {expectedId}, got:\n{Format(diagnostics)}");
    }

    static void AssertDoesNotFire(string source, string id)
    {
        var diagnostics = GeneratorTestHarness.RunAnalyzers(
            new DiagnosticAnalyzer[] { new ImmutableAnalyzer() },
            source
        );
        var hit = diagnostics.FirstOrDefault(d => d.Id == id);
        Assert.That(hit, Is.Null, $"Unexpected {id}: {hit}");
    }

    static string Format(ImmutableArray<Diagnostic> diagnostics)
    {
        if (diagnostics.IsEmpty)
            return "  (none)";
        return string.Join(
            "\n",
            diagnostics.Select(d =>
                $"  {d.Severity} {d.Id} at {d.Location.GetLineSpan()}: {d.GetMessage()}"
            )
        );
    }
}
