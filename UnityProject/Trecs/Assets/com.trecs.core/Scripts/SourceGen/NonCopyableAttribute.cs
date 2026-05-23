using System;

namespace Trecs
{
    /// <summary>
    /// Marks a <c>struct</c> whose value-identity matters — copying it to a by-value
    /// local or parameter is almost always a bug, because subsequent mutations land on
    /// the copy instead of the original.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Typical use: inline-storage containers like <c>FixedList256&lt;T&gt;</c> /
    /// <c>FixedArray256&lt;T&gt;</c>, whose data lives directly in the struct. Their
    /// mutating APIs use <c>ref this</c> extension methods, which already prevent
    /// mutation through an rvalue, but cannot prevent the caller from first assigning
    /// to a by-value local (<c>var copy = list; copy.Add(x);</c>) and then mutating
    /// the copy.
    /// </para>
    /// <para>
    /// The companion <c>NonCopyableAnalyzer</c> flags three patterns at compile time:
    /// <list type="bullet">
    ///   <item><b>TRECS118</b> — by-value local initialized from an existing variable
    ///   (field, local, parameter, or property). Initializing from a method return,
    ///   constructor, or <c>default</c> is allowed.</item>
    ///   <item><b>TRECS119</b> — by-value method parameter. Must be declared
    ///   <c>ref</c>, <c>in</c>, or <c>out</c>.</item>
    ///   <item><b>TRECS120</b> — the same struct also carries
    ///   <see cref="CopyableAttribute"/>; the two are contradictory.</item>
    /// </list>
    /// </para>
    /// <para>
    /// The rule propagates transitively through fields: a struct that contains a
    /// non-static instance field whose type is non-copyable is itself non-copyable,
    /// because copying the wrapper duplicates the inner storage the same way. To
    /// ship a wrapper of inline-storage data, either mark the wrapper
    /// <c>[NonCopyable]</c> too (acknowledging the constraint) or restructure so
    /// the non-copyable lives behind an indirection (e.g. <c>NativeUniquePtr</c>).
    /// </para>
    /// <para>
    /// <c>IEntityComponent</c> structs are non-copyable by default without needing
    /// this attribute — see <see cref="CopyableAttribute"/> for the opt-out.
    /// </para>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
    public sealed class NonCopyableAttribute : Attribute { }
}
