using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Burst;

namespace Trecs
{
    public class ComponentTypeId<T>
        where T : unmanaged, IEntityComponent
    {
        static readonly SharedStaticWrapper<ComponentId, ComponentTypeId<T>> _id;

        public static ComponentId Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _id.Data;
        }

        static ComponentTypeId()
        {
            Init();
        }

        public static void Warmup() { }

        [BurstDiscard]
        // SharedStatic values must be initialized from not burstified code
        internal static void Init()
        {
            if (_id.Data.Value != 0)
            {
                return;
            }

            _id.Data = new(TypeIdProvider.GetTypeId(typeof(T)));
        }
    }
}
