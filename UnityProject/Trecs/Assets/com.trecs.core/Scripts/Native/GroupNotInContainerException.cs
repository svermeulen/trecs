using System;

namespace Trecs
{
    /// <summary>
    /// Thrown when a Trecs cross-entity container (e.g.
    /// <see cref="NativeComponentLookupRead{T}"/>, <see cref="NativeComponentLookupWrite{T}"/>)
    /// is asked for an entity whose
    /// <see cref="Group"/> isn't in the container's permitted group set.
    /// <para>
    /// Inherits from <see cref="InvalidOperationException"/> so existing
    /// <c>catch (InvalidOperationException)</c> sites still work; the typed subclass lets
    /// callers that want to react specifically to the missing-group case do so without
    /// pattern-matching on exception messages.
    /// </para>
    /// </summary>
    public sealed class GroupNotInContainerException : InvalidOperationException
    {
        /// <summary>The name of the container type that threw (e.g. <c>NativeComponentLookupRead&lt;CPosition&gt;</c>).</summary>
        public string ContainerTypeName { get; }

        /// <summary>The numeric group id that wasn't in the container's permitted set.</summary>
        public int GroupId { get; }

        public GroupNotInContainerException(string containerTypeName, int groupId, string message)
            : base(message)
        {
            ContainerTypeName = containerTypeName;
            GroupId = groupId;
        }
    }
}
