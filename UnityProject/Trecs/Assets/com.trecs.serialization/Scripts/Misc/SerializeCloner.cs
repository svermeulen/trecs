namespace Trecs.Serialization
{
    public class SerializeCloner
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
                serializerHelper.ClearMemoryStream();
                serializerHelper.WriteAll(data, version: 1, includeTypeChecks: false);
                serializerHelper.ResetMemoryPosition();
                return serializerHelper.ReadAll<T>();
            }
        }
    }
}
