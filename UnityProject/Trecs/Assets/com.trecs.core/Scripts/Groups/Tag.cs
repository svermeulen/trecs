using System;
using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Burst;

namespace Trecs
{
    /// <summary>
    /// Lightweight semantic label used to classify entities into <see cref="GroupIndex"/>s.
    /// Each tag type (an <see cref="ITag"/> struct) maps to a unique integer <see cref="Guid"/>.
    /// Tags are combined into <see cref="TagSet"/>s to define groups; entities with the same
    /// tag combination share a group and its contiguous component buffers.
    /// Use <see cref="Tag{T}"/> for zero-allocation access to a tag's runtime value.
    /// </summary>
    public readonly struct Tag : IEquatable<Tag>, IStableHashProvider
    {
        /// <summary>
        /// Stable integer identifier for this tag, derived from the tag type's
        /// <see cref="TypeIdAttribute"/> or computed deterministically from its name.
        /// </summary>
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
    /// Zero-allocation cache for the <see cref="Tag"/> instance corresponding to an
    /// <see cref="ITag"/> struct type. Access the cached value via <c>Tag&lt;MyTag&gt;.Value</c>.
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
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
