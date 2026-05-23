using System;

namespace Trecs
{
    /// <summary>
    /// Opts a <c>struct</c> implementing <see cref="IEntityComponent"/> back into
    /// the C# default of free value-copy semantics.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All <c>IEntityComponent</c> structs are treated as non-copyable by default
    /// (enforced by <c>NonCopyableAnalyzer</c> — TRECS118 / TRECS119), reflecting
    /// the framework's invariant that components live in component buffers and are
    /// accessed by reference (via aspect properties, <c>NativeComponentLookup</c>
    /// indexers, etc.). Apply this attribute to small flat components — typed
    /// handles, configs, primitive wrappers — where copying is cheap and
    /// semantically meaningful (e.g. capturing a value snapshot for comparison).
    /// </para>
    /// <para>
    /// Applying <see cref="CopyableAttribute"/> and <see cref="NonCopyableAttribute"/>
    /// to the same type is a contradiction and is flagged as TRECS120.
    /// </para>
    /// <para>
    /// <c>[Copyable]</c> does <em>not</em> suppress the transitive non-copyability
    /// rule: a component marked <c>[Copyable]</c> that nevertheless contains a
    /// non-static instance field of a non-copyable type is still treated as
    /// non-copyable, because copying it would duplicate the inner storage the same
    /// way. The intended fix is to restructure (e.g. heap-indirect the non-copyable
    /// field) rather than to suppress the diagnostic.
    /// </para>
    /// <para>
    /// On a struct that is not an <c>IEntityComponent</c> this attribute is a no-op
    /// — non-component structs are already copyable by default.
    /// </para>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
    public sealed class CopyableAttribute : Attribute { }
}
