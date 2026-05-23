using System;

namespace Trecs
{
    /// <summary>
    /// Marks a class or interface as the immutable face of a managed shared
    /// blob. One of these must appear on the type argument <c>T</c> of
    /// <see cref="SharedPtr{T}"/>: managed shared blobs live in the
    /// <see cref="BlobCache"/>, which is not snapshotted alongside game-state,
    /// so any post-Alloc mutation silently desyncs determinism. The marker is
    /// opt-in: a class or interface without it cannot be stored behind a
    /// <c>SharedPtr</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two adoption paths.</b> Authors choose which fits the type:
    /// <list type="bullet">
    ///   <item><b>Class.</b> Mark the class <c>[Immutable]</c> directly. The
    ///   class itself is then audited for structural immutability — readonly
    ///   fields, no public setters, safe field types, etc. Best for small
    ///   leaf types built once via a constructor (e.g. a colour palette or
    ///   content descriptor).</item>
    ///   <item><b>Interface.</b> Declare an <c>IReadOnlyFoo</c> interface
    ///   marked <c>[Immutable]</c> and have the (mutable) concrete class
    ///   implement it. <c>SharedPtr&lt;IReadOnlyFoo&gt;</c> hands callers only
    ///   the read-only face; the concrete keeps whatever construction model
    ///   it already has (pool-allocated, deserialized in place, etc.). Best
    ///   for fat retrofit-heavy types whose Pool+Serializer lifecycle is
    ///   incompatible with field-level immutability.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Class audit (TRECS126).</b> When applied to a class the analyzer
    /// validates the canonical mistakes over deep semantic analysis:
    /// <list type="bullet">
    ///   <item>Every instance field must be declared <c>readonly</c>.</item>
    ///   <item>No publicly-settable property setters (auto or explicit);
    ///   <c>init</c>-only setters are allowed.</item>
    ///   <item>Public / internal / protected field types must be in the
    ///   "obviously immutable" set: primitives, <c>string</c>, enums,
    ///   <c>readonly struct</c>s (recursively), other <c>[Immutable]</c>
    ///   classes or interfaces (recursively), and BCL immutable / read-only
    ///   views like <c>ImmutableArray&lt;T&gt;</c>, <c>ReadOnlyMemory&lt;T&gt;</c>,
    ///   <c>ReadOnlyCollection&lt;T&gt;</c>, <c>IReadOnlyList&lt;T&gt;</c>,
    ///   <c>IReadOnlyDictionary&lt;K, V&gt;</c>, etc.</item>
    ///   <item>Private instance fields are intentionally not type-checked
    ///   — the canonical "wrap a mutable <c>T[]</c> as a private
    ///   <c>readonly</c> field and expose it as a <c>ReadOnlySpan&lt;T&gt;</c>
    ///   or <c>IReadOnlyList&lt;T&gt;</c>" pattern is allowed.</item>
    ///   <item>Base class (if not <c>object</c>) must also carry
    ///   <c>[Immutable]</c> directly — the marker does not inherit, so each
    ///   class in a chain opts in explicitly.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Interface audit (TRECS126).</b> When applied to an interface the
    /// analyzer enforces a smaller rule set — interfaces have no fields and
    /// no inheritance state of their own:
    /// <list type="bullet">
    ///   <item>No publicly-settable property accessors; <c>init</c>-only is
    ///   allowed.</item>
    ///   <item>No events.</item>
    ///   <item>Public property types must be in the same "obviously
    ///   immutable" set as for classes (recursively).</item>
    /// </list>
    /// Method-return immutability is enforced as a <i>warning</i> by
    /// <c>TRECS127</c>: a method on an <c>[Immutable]</c> interface that
    /// returns a non-safe type fires unless annotated with
    /// <c>[Trecs.AllowMutableReturn]</c>. The opt-out is declaration-local,
    /// so escape hatches stay visible at the interface file rather than
    /// being implicit looseness. <c>void</c> methods and non-ordinary
    /// methods (operators, accessors, etc.) are not checked. The exemption exists because
    /// interface methods can legitimately mean "live alias", "fresh
    /// defensive copy", "computed transformation", or "subset view", and
    /// the analyzer cannot tell which from a signature alone — but the
    /// warning surfaces the choice so the reviewer can.
    /// </para>
    /// <para>
    /// What the analyzer cannot catch:
    /// <list type="bullet">
    ///   <item>Aliasing across the constructor boundary — if the caller
    ///   keeps a reference to a mutable collection passed in, they can
    ///   mutate the blob after construction. Mitigations: take a
    ///   <c>ReadOnlySpan</c>/<c>IReadOnlyList</c> input and copy, or use an
    ///   immutable collection type for the parameter.</item>
    ///   <item>Downcast through an <c>[Immutable]</c> interface to the
    ///   underlying mutable concrete type. The interface route trusts the
    ///   convention "don't downcast"; tighten by making the concrete class
    ///   <c>internal</c> or <c>private</c> if the cost is worth it.</item>
    ///   <item>Reflection, <c>Unsafe.*</c>, or other API-bypass mechanisms.</item>
    ///   <item>Internal mutation through methods that mutate private
    ///   collections held in private fields — keeping those out of public
    ///   surface area is the developer's responsibility.</item>
    /// </list>
    /// </para>
    /// <para>
    /// The native counterpart is the <c>readonly struct</c> rule on
    /// <see cref="NativeSharedPtr{T}"/> (enforced by <c>TRECS124</c>) — the
    /// C# language provides the constraint directly for value types, so no
    /// attribute is needed there.
    /// </para>
    /// </remarks>
    // Inherited=false intentionally: derived classes / sub-interfaces may add new mutable
    // state, so each type must opt in for the analyzer to validate its own contents.
    // SharedPtr<B> over a B that doesn't declare [Immutable] directly is rejected even
    // when B's base does.
    [AttributeUsage(
        AttributeTargets.Class | AttributeTargets.Interface,
        AllowMultiple = false,
        Inherited = false
    )]
    public sealed class ImmutableAttribute : Attribute { }
}
