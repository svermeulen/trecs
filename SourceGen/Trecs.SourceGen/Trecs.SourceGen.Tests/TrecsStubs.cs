namespace Trecs.SourceGen.Tests;

/// <summary>
/// Hand-written stubs for the subset of the Trecs runtime API that source-generated code
/// references at compile time. Lives here so source-gen tests can compile generated output
/// without dragging in Unity (Unity assemblies aren't available in CI and the real Trecs
/// runtime depends on UnityEngine, Unity.Collections, Unity.Burst, etc.).
///
/// Coverage is deliberately incremental: types are added as new tests need them, not all
/// upfront. If a new compile-cleanliness test fails with "type X not found", add the
/// minimum stub here to make it resolve. Bodies are empty / trivial — these stubs only
/// need to satisfy the C# compiler's name and shape resolution, not actually run.
/// </summary>
internal static class TrecsStubs
{
    public const string Source = """
        // Polyfill for record types on test-time targets (kept for parity with the source-gen project).
        namespace System.Runtime.CompilerServices
        {
            internal static class IsExternalInit { }
        }

        // Empty namespace declarations so generated `using` directives resolve. The source-gen
        // project's CommonUsings emits `using Trecs;`, `using Trecs.Internal;`,
        // `using Trecs.Collections;` into every generated file — they all have
        // to exist as namespaces or every generated file fails to compile.
        namespace Trecs.Collections { }

        namespace Trecs
        {
            // Marker interfaces consumed by IncrementalEntityComponentGenerator and
            // IncrementalAspectGenerator base-list scanning.
            public interface IEntityComponent { }
            public interface ITag { }
            public interface IEntitySet { }
            public interface ITemplate { }

            public readonly struct GroupIndex
            {
                public int Index { get; }
                public bool IsNull { get; }
                public static GroupIndex Null { get; }
            }

            public readonly struct EntityIndex
            {
                public readonly int Index;
                public readonly GroupIndex GroupIndex;
                public static EntityIndex Null { get; }
                public bool IsNull { get; }
            }

            public readonly struct EntityHandle
            {
                public readonly int UniqueId;
                public readonly int Version;
                public static EntityHandle Null { get; }
                public bool IsNull { get; }
            }

            public readonly struct Tag { }
            public readonly struct TagSet { }

            // IAspect contract — see Packages/com.trecs.core/Scripts/SourceGen/IAspect.cs.
            // The EntityIndex member is required because aspect-interface generation depends on it.
            public interface IAspect { EntityIndex EntityIndex { get; } }

            // IRead/IWrite arity 1..8 — matches the real runtime. Generators emit references
            // up to this arity when an aspect declares many components.
            public interface IRead<T1> { }
            public interface IRead<T1, T2> { }
            public interface IRead<T1, T2, T3> { }
            public interface IRead<T1, T2, T3, T4> { }
            public interface IRead<T1, T2, T3, T4, T5> { }
            public interface IRead<T1, T2, T3, T4, T5, T6> { }
            public interface IRead<T1, T2, T3, T4, T5, T6, T7> { }
            public interface IRead<T1, T2, T3, T4, T5, T6, T7, T8> { }

            public interface IWrite<T1> { }
            public interface IWrite<T1, T2> { }
            public interface IWrite<T1, T2, T3> { }
            public interface IWrite<T1, T2, T3, T4> { }
            public interface IWrite<T1, T2, T3, T4, T5> { }
            public interface IWrite<T1, T2, T3, T4, T5, T6> { }
            public interface IWrite<T1, T2, T3, T4, T5, T6, T7> { }
            public interface IWrite<T1, T2, T3, T4, T5, T6, T7, T8> { }

            // Template composition interfaces — IncrementalTemplateDefinitionGenerator validates
            // their use; arity 1..4 mirrors TemplateAttributes.cs in the runtime.
            public interface IExtends<T1> where T1 : class, ITemplate { }
            public interface IExtends<T1, T2> where T1 : class, ITemplate where T2 : class, ITemplate { }
            public interface IExtends<T1, T2, T3> where T1 : class, ITemplate where T2 : class, ITemplate where T3 : class, ITemplate { }
            public interface IExtends<T1, T2, T3, T4> where T1 : class, ITemplate where T2 : class, ITemplate where T3 : class, ITemplate where T4 : class, ITemplate { }

            // [Unwrap] — applied to single-field IEntityComponent structs to opt into
            // an emitted convenience constructor.
            [System.AttributeUsage(System.AttributeTargets.Struct, AllowMultiple = false)]
            public class UnwrapAttribute : System.Attribute { }
        }

        namespace Trecs.Internal
        {
            // Real signature lives at Packages/com.trecs.core/Scripts/Util/UnmanagedUtil.cs.
            // Generated equality / operator overloads in IncrementalEntityComponentGenerator
            // call BlittableEquals; the body doesn't matter for compile-cleanliness tests.
            public static class UnmanagedUtil
            {
                public static bool BlittableEquals<T>(in T lhs, in T rhs) where T : unmanaged => false;
            }
        }
        """;
}
