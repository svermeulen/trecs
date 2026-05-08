using System;

namespace Trecs
{
    /// <summary>
    /// Specifies an explicit GUID for a tag type, overriding the auto-generated hash.
    /// Use this to preserve backward-compatible tag IDs when migrating from explicit Tag declarations.
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct, AllowMultiple = false)]
    public sealed class TagIdAttribute : Attribute
    {
        public int Id { get; }

        public TagIdAttribute(int id)
        {
            Id = id;
        }
    }

    /// <summary>
    /// Marker interface for tag types. Tags are empty structs that classify entities into
    /// <see cref="GroupIndex"/>s. Implement on a struct to define a new tag:
    /// <c>struct Doofus : ITag {}</c>
    /// </summary>
    public interface ITag { }

    /// <summary>
    /// Marker interface for entity template declarations. A template is a class whose
    /// fields define the components an entity carries. The source generator emits
    /// builder code and registration helpers. Register templates via
    /// <see cref="WorldBuilder.AddTemplate"/>.
    /// </summary>
    public interface ITemplate { }

    /// <summary>
    /// Declares that a template extends (inherits from) one base template.
    /// </summary>
    public interface IExtends<T1>
        where T1 : class, ITemplate { }

    /// <inheritdoc cref="IExtends{T1}"/>
    public interface IExtends<T1, T2>
        where T1 : class, ITemplate
        where T2 : class, ITemplate { }

    /// <inheritdoc cref="IExtends{T1}"/>
    public interface IExtends<T1, T2, T3>
        where T1 : class, ITemplate
        where T2 : class, ITemplate
        where T3 : class, ITemplate { }

    /// <inheritdoc cref="IExtends{T1}"/>
    public interface IExtends<T1, T2, T3, T4>
        where T1 : class, ITemplate
        where T2 : class, ITemplate
        where T3 : class, ITemplate
        where T4 : class, ITemplate { }

    /// <summary>
    /// Declares tags on a template. Type args must implement ITag.
    /// </summary>
    public interface ITagged<T1>
        where T1 : struct, ITag { }

    /// <inheritdoc cref="ITagged{T1}"/>
    public interface ITagged<T1, T2>
        where T1 : struct, ITag
        where T2 : struct, ITag { }

    /// <inheritdoc cref="ITagged{T1}"/>
    public interface ITagged<T1, T2, T3>
        where T1 : struct, ITag
        where T2 : struct, ITag
        where T3 : struct, ITag { }

    /// <inheritdoc cref="ITagged{T1}"/>
    public interface ITagged<T1, T2, T3, T4>
        where T1 : struct, ITag
        where T2 : struct, ITag
        where T3 : struct, ITag
        where T4 : struct, ITag { }

    /// <summary>
    /// Declares a valid partition on a template. Each IHasPartition implementation
    /// represents one valid partition. Type args are tag types that form the partition's TagSet.
    /// </summary>
    public interface IHasPartition<T1>
        where T1 : struct, ITag { }

    /// <inheritdoc cref="IHasPartition{T1}"/>
    public interface IHasPartition<T1, T2>
        where T1 : struct, ITag
        where T2 : struct, ITag { }

    /// <inheritdoc cref="IHasPartition{T1}"/>
    public interface IHasPartition<T1, T2, T3>
        where T1 : struct, ITag
        where T2 : struct, ITag
        where T3 : struct, ITag { }

    /// <inheritdoc cref="IHasPartition{T1}"/>
    public interface IHasPartition<T1, T2, T3, T4>
        where T1 : struct, ITag
        where T2 : struct, ITag
        where T3 : struct, ITag
        where T4 : struct, ITag { }

    /// <summary>
    /// Base interface for set types. Can be implemented directly (without tag parameters)
    /// to create a global set valid for all groups.
    /// Usage: struct SelectedEntities : IEntitySet { }
    /// </summary>
    public interface IEntitySet { }

    /// <summary>
    /// Declares a set scoped to one or more tags. Sets track subsets of entities within
    /// the matching groups. They are automatically maintained when entities are removed
    /// or swapped, and their contents are serialized with game state.
    /// Must be registered on the WorldBuilder via AddSet&lt;T&gt;().
    /// Usage: struct EatingDoofus : IEntitySet&lt;DoofusTags.Doofus&gt; { }
    /// </summary>
    public interface IEntitySet<T1> : IEntitySet
        where T1 : struct, ITag { }

    /// <inheritdoc cref="IEntitySet{T1}"/>
    public interface IEntitySet<T1, T2> : IEntitySet
        where T1 : struct, ITag
        where T2 : struct, ITag { }

    /// <inheritdoc cref="IEntitySet{T1}"/>
    public interface IEntitySet<T1, T2, T3> : IEntitySet
        where T1 : struct, ITag
        where T2 : struct, ITag
        where T3 : struct, ITag { }

    /// <inheritdoc cref="IEntitySet{T1}"/>
    public interface IEntitySet<T1, T2, T3, T4> : IEntitySet
        where T1 : struct, ITag
        where T2 : struct, ITag
        where T3 : struct, ITag
        where T4 : struct, ITag { }

    /// <summary>
    /// Specifies an explicit stable ID for a set type, overriding the auto-generated hash.
    /// Similar to TagIdAttribute for tags.
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct, AllowMultiple = false)]
    public sealed class SetIdAttribute : Attribute
    {
        public int Id { get; }

        public SetIdAttribute(int id)
        {
            Id = id;
        }
    }

    /// <summary>
    /// Marks a template component field as interpolated. The framework stores a previous-frame
    /// snapshot and blends between it and the current value each rendered frame, allowing smooth
    /// visual motion at variable frame rates while the simulation runs at a fixed time step.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public sealed class InterpolatedAttribute : Attribute { }

    /// <summary>
    /// Restricts a template component field or an entire template to the variable-update
    /// phase only. Variable / Input / Unrestricted roles may read and write the affected state
    /// freely; Fixed-role systems are rejected at access time.
    /// <para>
    /// On a <b>component field</b>: the single component is render-cadence; the rest of
    /// the template is unaffected.
    /// </para>
    /// <para>
    /// On a <b>template class</b>: every component on the template is treated as
    /// <c>[VariableUpdateOnly]</c>, and Fixed-role queries that resolve to any of the
    /// template's groups are rejected. The template's component arrays are still
    /// snapshot/restore round-tripped, but skipped during the determinism checksum walk.
    /// Use this for entities that are pure render-side state (cameras, view-only
    /// helpers) so structural changes can happen on the variable side.
    /// </para>
    /// <para>
    /// Not applicable to entity sets — set membership is always part of deterministic
    /// simulation state. For render-cadence collections of entity references, use a
    /// plain managed collection on a Variable / Input service rather than a trecs set.
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Class, AllowMultiple = false)]
    public sealed class VariableUpdateOnlyAttribute : Attribute { }

    /// <summary>
    /// Marks a template component field as constant. The value is set once during entity
    /// initialization and cannot be modified afterward. Attempts to write produce a runtime error.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public sealed class ConstantAttribute : Attribute { }

    /// <summary>
    /// Marks a template component field as externally-driven input. Input components are
    /// written by <see cref="SystemPhase.Input"/> systems via <see cref="WorldAccessor.AddInput{T}"/>
    /// and consumed by fixed-update systems. See <see cref="MissingInputBehavior"/> for
    /// what happens when no input is provided for a frame.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public sealed class InputAttribute : Attribute
    {
        public MissingInputBehavior OnMissing { get; }
        public bool WarnOnMissing { get; }

        public InputAttribute(MissingInputBehavior onMissing, bool warnOnMissing = false)
        {
            OnMissing = onMissing;
            WarnOnMissing = warnOnMissing;
        }
    }
}
