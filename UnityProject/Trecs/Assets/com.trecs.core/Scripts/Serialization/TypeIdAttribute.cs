using System;

namespace Trecs
{
    /// <summary>
    /// Assigns a stable serialization type ID to a component struct.
    /// Required for deterministic serialization of ComponentId values.
    /// Use a random int to ensure uniqueness across the codebase.
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class | AttributeTargets.Enum)]
    public sealed class TypeIdAttribute : Attribute
    {
        public int Id { get; }

        public TypeIdAttribute(int id)
        {
            Id = id;
        }
    }
}
