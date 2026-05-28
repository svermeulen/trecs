using System;

namespace Trecs
{
    /// <summary>
    /// Shorthand for <c>[FromSingleEntity(typeof(TrecsTags.Globals))]</c>. Resolves
    /// the unique entity tagged with <see cref="TrecsTags.Globals"/> and binds it
    /// to the annotated parameter or field.
    /// </summary>
    [AttributeUsage(
        AttributeTargets.Parameter | AttributeTargets.Field,
        AllowMultiple = false,
        Inherited = false
    )]
    public sealed class FromGlobalEntityAttribute : Attribute { }
}
