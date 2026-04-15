using System;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs.Serialization
{
    public class UniqueHashGenerator : IDisposable
    {
        static readonly TrecsLog _log = new(nameof(UniqueHashGenerator));
        readonly SerializationBuffer _serializeHelper;

        public UniqueHashGenerator(SerializerRegistry serializationManager)
        {
            _serializeHelper = new(serializationManager);
        }

        public void Dispose()
        {
            _serializeHelper.Dispose();
        }

        /// <summary>
        /// Note that T must be serialzable for this to work
        /// </summary>
        public long Generate<T>(in T input, ReadOnlyDenseHashSet<int> flags = default)
        {
            using (TrecsProfiling.Start("Generating guid for type {}", typeof(T)))
            {
                _serializeHelper.ClearMemoryStream();

                // Note that we enable includeTypeChecks here
                // This is nice because it will produce different blob ids
                // for different types even when binary data otherwise matches
                // Check IsValueType here to avoid boxing
                if (typeof(T).IsValueType)
                {
                    _serializeHelper.WriteAll<T>(
                        input,
                        version: 1,
                        includeTypeChecks: true,
                        flags: flags
                    );
                }
                else
                {
                    _serializeHelper.WriteAllObject(
                        input,
                        version: 1,
                        includeTypeChecks: true,
                        flags: flags
                    );
                }

                _serializeHelper.ResetMemoryPosition();

                var hash = _serializeHelper.GetMemoryStreamCollisionResistantHash();
                if (hash == 0)
                    hash = 1; // Reserve 0 for null
                return hash;
            }
        }
    }
}
