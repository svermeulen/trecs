using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Burst;

namespace Trecs
{
    /// <summary>
    /// Zero-allocation cache for the <see cref="ComponentId"/> of a component type.
    /// Access via <c>ComponentTypeId&lt;MyComponent&gt;.Value</c>. The ID is derived from the
    /// type's <see cref="TypeIdAttribute"/> and stored in a <c>SharedStatic</c> for Burst compatibility.
    /// </summary>
    public sealed class ComponentTypeId<T>
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
