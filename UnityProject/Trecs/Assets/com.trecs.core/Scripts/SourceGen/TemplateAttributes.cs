using System;

namespace Trecs
{
    /// <summary>
    /// Specifies an explicit GUID for a tag type, overriding the auto-generated hash.
    /// Use this to preserve backward-compatible tag IDs when migrating from explicit Tag declarations.
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct, AllowMultiple = false)]
    public class TagIdAttribute : Attribute
    {
        public int Id { get; }

        public TagIdAttribute(int id)
        {
            Id = id;
        }
    }

    /// <summary>
    /// Marker interface for tag types. Tags are empty structs implementing this interface.
    /// Example: struct Doofus : ITag {}
    /// </summary>
    public interface ITag { }

    /// <summary>
    /// Marker interface for template declarations with no base templates.
    /// Struct fields define components; source generator emits the builder code.
    /// </summary>
    public interface ITemplate { }

    /// <summary>
    /// Declares that a template extends (inherits from) one base template.
    /// </summary>
    public interface IExtends<T1>
        where T1 : class, ITemplate { }

    /// <summary>
    /// Declares that a template extends (inherits from) two base templates.
    /// </summary>
    public interface IExtends<T1, T2>
        where T1 : class, ITemplate
        where T2 : class, ITemplate { }

    /// <summary>
    /// Declares that a template extends (inherits from) three base templates.
    /// </summary>
    public interface IExtends<T1, T2, T3>
        where T1 : class, ITemplate
        where T2 : class, ITemplate
        where T3 : class, ITemplate { }

    /// <summary>
    /// Declares that a template extends (inherits from) four base templates.
    /// </summary>
    public interface IExtends<T1, T2, T3, T4>
        where T1 : class, ITemplate
        where T2 : class, ITemplate
        where T3 : class, ITemplate
        where T4 : class, ITemplate { }

    /// <summary>
    /// Declares tags on a template. Type args must implement ITag.
    /// </summary>
    public interface IHasTags<T1>
        where T1 : struct, ITag { }

    /// <summary>
    /// Declares tags on a template with two tag types.
    /// </summary>
    public interface IHasTags<T1, T2>
        where T1 : struct, ITag
        where T2 : struct, ITag { }

    /// <summary>
    /// Declares tags on a template with three tag types.
    /// </summary>
    public interface IHasTags<T1, T2, T3>
        where T1 : struct, ITag
        where T2 : struct, ITag
        where T3 : struct, ITag { }

    /// <summary>
    /// Declares tags on a template with four tag types.
    /// </summary>
    public interface IHasTags<T1, T2, T3, T4>
        where T1 : struct, ITag
        where T2 : struct, ITag
        where T3 : struct, ITag
        where T4 : struct, ITag { }

    /// <summary>
    /// Declares a valid state combination on a template. Each IHasState implementation
    /// represents one valid state. Type args are tag types that form the state's TagSet.
    /// </summary>
    public interface IHasState<T1>
        where T1 : struct, ITag { }

    /// <summary>
    /// Declares a state combination with two tags.
    /// </summary>
    public interface IHasState<T1, T2>
        where T1 : struct, ITag
        where T2 : struct, ITag { }

    /// <summary>
    /// Declares a state combination with three tags.
    /// </summary>
    public interface IHasState<T1, T2, T3>
        where T1 : struct, ITag
        where T2 : struct, ITag
        where T3 : struct, ITag { }

    /// <summary>
    /// Declares a state combination with four tags.
    /// </summary>
    public interface IHasState<T1, T2, T3, T4>
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

    /// <summary>
    /// Declares a set scoped to two tags.
    /// </summary>
    public interface IEntitySet<T1, T2> : IEntitySet
        where T1 : struct, ITag
        where T2 : struct, ITag { }

    /// <summary>
    /// Declares a set scoped to three tags.
    /// </summary>
    public interface IEntitySet<T1, T2, T3> : IEntitySet
        where T1 : struct, ITag
        where T2 : struct, ITag
        where T3 : struct, ITag { }

    /// <summary>
    /// Declares a set scoped to four tags.
    /// </summary>
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
    public class SetIdAttribute : Attribute
    {
        public int Id { get; }

        public SetIdAttribute(int id)
        {
            Id = id;
        }
    }

    /// <summary>
    /// Marks a component field as interpolated. The source generator will emit .Interpolated() on the component builder.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public class InterpolatedAttribute : Attribute { }

    /// <summary>
    /// Marks a component field as fixed-update-only.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public class FixedUpdateOnlyAttribute : Attribute { }

    /// <summary>
    /// Marks a component field as variable-update-only.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public class VariableUpdateOnlyAttribute : Attribute { }

    /// <summary>
    /// Marks a component field as constant (immutable once set).
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public class ConstantAttribute : Attribute { }

    /// <summary>
    /// Marks a component field as an input component.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public class InputAttribute : Attribute
    {
        public MissingInputFrameBehaviour InputFrameBehaviour { get; }
        public bool WarnOnMissing { get; }

        public InputAttribute(
            MissingInputFrameBehaviour inputFrameBehaviour,
            bool warnOnMissing = false
        )
        {
            InputFrameBehaviour = inputFrameBehaviour;
            WarnOnMissing = warnOnMissing;
        }
    }
}
