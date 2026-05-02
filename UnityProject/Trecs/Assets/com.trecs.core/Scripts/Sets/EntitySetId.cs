using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Burst;

namespace Trecs
{
    /// <summary>
    /// Burst-compatible cache for SetId values derived from IEntitySet struct types.
    /// Uses SharedStatic to avoid exposing managed types (EntitySet contains a string)
    /// to the Burst compiler.
    /// </summary>
    public static class EntitySetId<T>
        where T : struct, IEntitySet
    {
        struct Key { }

        static readonly SharedStaticWrapper<SetId, Key> _nativeSetId;

        public static SetId Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _nativeSetId.Data;
        }

        static EntitySetId()
        {
            Init();
        }

        [BurstDiscard]
        static void Init()
        {
            if (_nativeSetId.Data.Id != 0)
            {
                return;
            }

            _nativeSetId.Data = EntitySet<T>.Value.Id;
        }
    }
}
