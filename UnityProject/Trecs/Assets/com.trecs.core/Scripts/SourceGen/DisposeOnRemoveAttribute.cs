using System;

namespace Trecs
{
    /// <summary>
    /// Marks a heap-backed component field so its backing storage is
    /// automatically disposed when the entity owning the component is removed.
    /// Trecs does not otherwise auto-dispose component fields — without this
    /// attribute (or a manual <c>OnRemoved</c> handler) the storage leaks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Supported field types — the generated handler calls
    /// <c>field.Dispose(world)</c> uniformly, and each type's own
    /// <c>Dispose</c> does the right thing:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><c>TrecsList&lt;T&gt;</c>,
    ///     <c>UniquePtr&lt;T&gt;</c>, <c>NativeUniquePtr&lt;T&gt;</c> —
    ///     <b>free</b> the backing storage.</description></item>
    ///   <item><description><c>SharedPtr&lt;T&gt;</c>,
    ///     <c>NativeSharedPtr&lt;T&gt;</c> — <b>decrement</b> the reference
    ///     count, freeing only when it reaches zero.</description></item>
    /// </list>
    /// <para>
    /// Disposal runs strictly after every user <c>OnRemoved</c> callback and
    /// every <see cref="CascadeRemoveAttribute"/> handler for the submission's
    /// removed entities, so a callback that reads the field (directly or across
    /// an <see cref="EntityHandle"/>) never observes freed storage. A field
    /// marked both <see cref="CascadeRemoveAttribute"/> and
    /// <see cref="DisposeOnRemoveAttribute"/> is read for the cascade before it is
    /// disposed.
    /// </para>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
    public sealed class DisposeOnRemoveAttribute : Attribute { }
}
