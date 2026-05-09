using System.ComponentModel;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class SerializeCloner
    {
        readonly SharedSerializerCacheHelper _sharedSerializerHelper;

        public SerializeCloner(SharedSerializerCacheHelper sharedSerializerHelper)
        {
            _sharedSerializerHelper = sharedSerializerHelper;
        }

        public T Clone<T>(in T data)
        {
            using (_sharedSerializerHelper.Borrow(out var serializerHelper))
            {
                // Order matters: WriteAll moves the stream cursor to the end
                // of the written payload, so ResetMemoryPosition is required
                // before ReadAll can start at the beginning.
                serializerHelper.ClearMemoryStream();
                serializerHelper.WriteAll(data, version: 1, includeTypeChecks: false);
                serializerHelper.ResetMemoryPosition();
                return serializerHelper.ReadAll<T>();
            }
        }
    }
}
