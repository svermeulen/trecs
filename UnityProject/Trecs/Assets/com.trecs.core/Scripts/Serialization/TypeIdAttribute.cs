using System;

namespace Trecs
{
    [AttributeUsage(
        AttributeTargets.Struct
            | AttributeTargets.Class
            | AttributeTargets.Enum
            | AttributeTargets.Interface
    )]
    public sealed class TypeIdAttribute : Attribute
    {
        public int Id { get; }

        public TypeIdAttribute(int id)
        {
            Id = id;
        }
    }
}
