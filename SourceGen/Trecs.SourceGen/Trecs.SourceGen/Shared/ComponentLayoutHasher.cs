#nullable enable

using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Trecs.SourceGen.Shared
{
    /// <summary>
    /// Computes a compile-time layout hash for an <c>IEntityComponent</c> struct, used to
    /// close the "same-size field edit" gap in <c>WorldSchemaFingerprint</c>: the runtime
    /// fingerprint mixes each component's <c>UnsafeUtility.SizeOf</c>, which catches any
    /// edit that changes the struct size, but a field swap that preserves total size
    /// (e.g. <c>float X; float Y;</c> → <c>float Y; float X;</c>, or reordering an
    /// <c>int</c> and a <c>float</c>) fingerprints identically — and a pre-edit snapshot
    /// then blits its bytes into the wrong fields.
    ///
    /// <para>
    /// The generator emits this hash as a <c>const ulong</c> on the component partial
    /// (see <c>EntityComponentGenerator</c>), and the runtime fingerprint calculator
    /// reads it via reflection. Computing it at compile time means zero runtime cost and
    /// full IL2CPP safety — reflection-walking field layouts at runtime was rejected as
    /// fragile under code stripping.
    /// </para>
    ///
    /// <para>
    /// The hash covers each instance field's <i>type</i> in declaration order, recursing
    /// into nested struct fields so a nested struct's internal layout participates too.
    /// It deliberately does <b>not</b> include field <i>names</i>: a pure field rename
    /// preserves the blit layout, so renames stay snapshot-compatible (the residual gap
    /// the original size-only hash leaves is the byte placement, not the identifier).
    /// </para>
    /// </summary>
    internal static class ComponentLayoutHasher
    {
        // FNV-1a 64-bit. Self-contained (no Roslyn/runtime hash dependency) so the
        // value is a stable function of the canonical string below and reproduces
        // identically across generator builds.
        const ulong FnvOffsetBasis = 14695981039346656037UL;
        const ulong FnvPrime = 1099511628257UL;

        /// <summary>
        /// Builds the layout hash for <paramref name="componentType"/>. Returns a stable
        /// 64-bit value derived from the ordered sequence of (recursively expanded) field
        /// type names.
        /// </summary>
        public static ulong Compute(INamedTypeSymbol componentType)
        {
            var sb = new StringBuilder();
            // Guard against cyclic struct graphs (illegal in C#, but a malformed
            // in-progress edit can momentarily present one to the analyzer). The
            // recursion records each type currently on the stack and emits a marker
            // instead of recursing again, so the hash stays defined rather than
            // overflowing.
            var visiting = new HashSet<string>();
            AppendLayout(componentType, sb, visiting);
            return Fnv1a(sb.ToString());
        }

        static void AppendLayout(ITypeSymbol type, StringBuilder sb, HashSet<string> visiting)
        {
            // A struct whose fields are all primitives/enums/type-parameters bottoms out
            // here as its display name. Recursing into nested *struct* fields means a
            // same-size reorder inside the nested struct also changes the hash.
            string display = type.ToDisplayString();

            // Only recurse into nested user structs. Primitives, enums, pointers, and
            // type parameters are leaves — their display name alone identifies their blit
            // contribution. Recursing past a named struct that we are already expanding
            // would loop on a (malformed) cyclic graph.
            bool recurse =
                type is INamedTypeSymbol named
                && type.TypeKind == TypeKind.Struct
                && !type.IsTupleType
                && !IsLeafStruct(named)
                && visiting.Add(display);

            if (!recurse)
            {
                sb.Append('<').Append(display).Append('>');
                return;
            }

            sb.Append('{').Append(display).Append(':');
            foreach (var field in EnumerateLayoutFields((INamedTypeSymbol)type))
            {
                AppendLayout(field.Type, sb, visiting);
                sb.Append(';');
            }
            sb.Append('}');

            visiting.Remove(display);
        }

        // Instance, non-const fields in source declaration order — the same fields the
        // blit covers, in the same order the compiler lays them out (sequential layout
        // is the default for structs without [StructLayout]).
        static IEnumerable<IFieldSymbol> EnumerateLayoutFields(INamedTypeSymbol type)
        {
            foreach (var member in type.GetMembers())
            {
                if (
                    member is IFieldSymbol field
                    && !field.IsStatic
                    && !field.IsConst
                    && !field.IsImplicitlyDeclared
                )
                {
                    yield return field;
                }
            }
        }

        // Treat framework/known-blittable structs (Unity math types, fixed-size buffers
        // we can't see into, etc.) as leaves: we can't reliably enumerate their private
        // fields from source, and their identity + the parent's size already pins them.
        // A struct in source we *can* see is not a leaf and gets expanded.
        static bool IsLeafStruct(INamedTypeSymbol type)
        {
            // Enums are TypeKind.Enum, handled by the TypeKind.Struct check; this guards
            // the case where a struct has no source-visible instance fields to expand
            // (e.g. a type from a referenced assembly with no metadata fields exposed, or
            // a genuinely empty struct). Nothing to recurse into → leaf.
            foreach (var member in type.GetMembers())
            {
                if (
                    member is IFieldSymbol field
                    && !field.IsStatic
                    && !field.IsConst
                    && !field.IsImplicitlyDeclared
                )
                {
                    return false;
                }
            }
            return true;
        }

        static ulong Fnv1a(string text)
        {
            ulong hash = FnvOffsetBasis;
            // Hash the UTF-16 code units; the string is ASCII-only canonical content
            // (type display names), so this is stable and platform-independent.
            for (int i = 0; i < text.Length; i++)
            {
                hash ^= text[i];
                hash *= FnvPrime;
            }
            return hash;
        }
    }
}
