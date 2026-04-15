using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs
{
    public readonly ref struct NativeEntityInitializer
    {
        readonly NativeBag _unsafeBuffer;
        readonly UnsafeArrayIndex _componentsToInitializeCounterRef;
        readonly EntityHandle _id;

        public NativeEntityInitializer(
            in NativeBag unsafeBuffer,
            UnsafeArrayIndex componentsToInitializeCounterRef,
            EntityHandle id
        )
        {
            _unsafeBuffer = unsafeBuffer;
            _componentsToInitializeCounterRef = componentsToInitializeCounterRef;
            _id = id;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NativeEntityInitializer Set<T>(in T component)
            where T : unmanaged, IEntityComponent
        {
            var componentId = ComponentTypeId<T>.Value;

            _unsafeBuffer.AccessReserved<uint>(_componentsToInitializeCounterRef)++; //increase the number of components that have been initialised by the user

            //Since NativeEntityInitializer is a ref struct, it guarantees that I am enqueueing components of the
            //last entity built
            _unsafeBuffer.Enqueue(componentId); //to know what component it's being stored
            _unsafeBuffer.ReserveEnqueue<T>(out _) = component;

            return this;
        }

        public EntityHandle Handle => _id;
    }
}
