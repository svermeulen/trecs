using System;
using Trecs.Internal;
using Unity.Burst;

namespace Trecs
{
    public readonly struct Tag : IEquatable<Tag>, IStableHashProvider
    {
        public readonly int Guid;

        public Tag(int guid)
        {
            Guid = guid;
        }

        public Tag(int guid, string debugName)
        {
            GroupTagNames.StoreName(guid, debugName);
            Guid = guid;
        }

        public override readonly bool Equals(object obj)
        {
            return obj is Tag other && Equals(other);
        }

        public readonly bool Equals(Tag other)
        {
            return Guid == other.Guid;
        }

        public override readonly int GetHashCode()
        {
            return GetStableHashCode();
        }

        public readonly int GetStableHashCode()
        {
            return Guid;
        }

        public override readonly string ToString()
        {
            return GroupTagNames.GetName(Guid);
        }

        public static bool operator ==(Tag c1, Tag c2)
        {
            return c1.Equals(c2);
        }

        public static bool operator !=(Tag c1, Tag c2)
        {
            return !c1.Equals(c2);
        }
    }

    /// <summary>
    /// Generic cache for Tag values derived from ITag struct types.
    /// Provides zero-allocation access to pre-computed Tag instances.
    /// Usage: Tag&lt;Red&gt;.Value
    /// </summary>
    public static class Tag<T>
        where T : struct, ITag
    {
        struct Key { }

        static readonly SharedStaticWrapper<int, Key> _nativeGuid;
        static Tag _value;

        public static Tag Value
        {
            get { return _value; }
        }

        /// <summary>
        /// Burst-compatible access to this tag's Guid via SharedStatic.
        /// Automatically warmed up when Tag&lt;T&gt;.Value is first accessed from managed code.
        /// </summary>
        public static int NativeGuid
        {
            [System.Runtime.CompilerServices.MethodImpl(
                System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining
            )]
            get => _nativeGuid.Data;
        }

        static Tag()
        {
            Init();
        }

        [BurstDiscard]
        static void Init()
        {
            if (_nativeGuid.Data != 0)
            {
                return;
            }

            _value = TagFactory.CreateTag(typeof(T));
            _nativeGuid.Data = _value.Guid;
        }
    }
}
