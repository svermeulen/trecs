using System;
using System.ComponentModel;
using System.Diagnostics;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class TypeRefWrapper<T>
    {
        public static RefWrapperType wrapper = new(typeof(T));
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    [DebuggerDisplay("{_type}")]
    public readonly struct RefWrapperType : IEquatable<RefWrapperType>
    {
        public RefWrapperType(Type type)
        {
            _type = type;
        }

        public bool Equals(RefWrapperType other)
        {
            return _type == other._type;
        }

        public override int GetHashCode()
        {
            return _type.GetHashCode();
        }

        public static implicit operator Type(RefWrapperType t) => t._type;

        readonly Type _type;
    }
}
