using System.ComponentModel;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface IInputHistoryLocker
    {
        int? MaxClearFrame { get; }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    internal interface IEntityInputQueue
    {
        void ClearFutureInputsAfterOrAt(int frame);
        void ClearInputsBeforeOrAt(int frame);
        void ClearAllInputs();

        // opaqueBlobStore/opaqueBaker carry opaque (eager) input-blob bytes for recording
        // persistence; null (the default) leaves such blobs unpersisted, as before.
        void Serialize(
            ISerializationWriter writer,
            IOpaqueBlobStore opaqueBlobStore = null,
            OpaqueBlobBaker opaqueBaker = null
        );
        void Deserialize(
            ISerializationReader reader,
            IOpaqueBlobStore opaqueBlobStore = null,
            OpaqueBlobBaker opaqueBaker = null
        );
        void AddHistoryLocker(IInputHistoryLocker locker);
        void RemoveHistoryLocker(IInputHistoryLocker locker);
        bool HasInputFrame<T>(int frame, EntityHandle entityHandle)
            where T : unmanaged, IEntityComponent;
        bool TryGetInput<T>(int frame, EntityHandle entityHandle, out T value)
            where T : unmanaged, IEntityComponent;
        ref T GetInputRefUnsafe<T>(int frame, EntityHandle entityHandle)
            where T : unmanaged, IEntityComponent;
        void AddInput<T>(int frame, EntityHandle entityHandle, in T value)
            where T : unmanaged, IEntityComponent;
        void SetInput<T>(int frame, EntityHandle entityHandle, in T value)
            where T : unmanaged, IEntityComponent;
        int GetMaxClearFrame(int currentFixedFrame);
    }
}
