using System;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Serializes a value with <see cref="SerializerRegistry"/> and produces a
    /// collision-resistant 64-bit hash of the resulting bytes. Useful for deriving
    /// stable, content-addressable identifiers from typed inputs — either the
    /// "recipe" of an expensive computation (so the result can be cached before
    /// the work is done) or the raw output itself.
    /// </summary>
    public sealed class UniqueHashGenerator : IDisposable
    {
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
        /// Serialize <paramref name="input"/> and return a 64-bit content hash.
        /// <typeparamref name="T"/> must be registered for serialization.
        /// </summary>
        public long Generate<T>(in T input, long flags = 0)
        {
            // The buffer is stateful and reused per call, so this must be a
            // single-threaded operation.
            TrecsAssert.That(
                UnityThreadHelper.IsMainThread,
                "UniqueHashGenerator is main-thread only"
            );

            using (TrecsProfiling.Start("Generating guid for type {0}", typeof(T)))
            {
                _serializeHelper.ClearMemoryStream();

                // Enable includeTypeChecks so that two values with identical binary
                // representations but different types produce different hashes.
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
