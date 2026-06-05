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
    public sealed class UniqueHashGenerator
    {
        readonly SerializationHelper _helper;

        // Reusable two-section serialize buffer; the writer clears it on each Generate.
        readonly SerializationData _data = new();

        public UniqueHashGenerator(SerializerRegistry serializationManager)
        {
            _helper = new SerializationHelper(serializationManager);
        }

        /// <summary>
        /// Serialize <paramref name="input"/> and return a 64-bit content hash.
        /// <typeparamref name="T"/> must be registered for serialization.
        /// </summary>
        public long Generate<T>(in T input, long flags = 0)
        {
            // The buffer is stateful and reused per call, so this must be a
            // single-threaded operation.
            TrecsDebugAssert.That(
                UnityThreadHelper.IsMainThread,
                "UniqueHashGenerator is main-thread only"
            );

            using (TrecsProfiling.Start("Generating guid for type {0}", typeof(T)))
            {
                // Enable includeTypeChecks so that two values with identical binary
                // representations but different types produce different hashes.
                if (typeof(T).IsValueType)
                {
                    _helper.WriteAll<T>(
                        _data,
                        input,
                        version: 1,
                        includeTypeChecks: true,
                        flags: flags
                    );
                }
                else
                {
                    _helper.WriteAllObject(
                        _data,
                        input,
                        version: 1,
                        includeTypeChecks: true,
                        flags: flags
                    );
                }

                // Hash the contiguous wire form in place — no intermediate contiguous byte[].
                // ComputeContiguousChecksum is byte-identical to hashing the materialized contiguous
                // form (see XxHash64Builder), so cached ids — including DiskMemoize's persistent disk
                // keys — stay stable across the move off SerializationBuffer.
                var hash = unchecked((long)_data.ComputeContiguousChecksum());
                if (hash == 0)
                    hash = 1; // Reserve 0 for null
                return hash;
            }
        }
    }
}
