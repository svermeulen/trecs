using System;

namespace Trecs.Internal
{
    /// <summary>
    /// Pairs a <see cref="BinarySerializationReader"/> and <see cref="BinarySerializationWriter"/>
    /// (both bound to one <see cref="SerializerRegistry"/>) and offers whole-payload round-trip
    /// helpers. Unlike the <c>SerializationBuffer</c> convenience it owns no byte storage: the caller supplies
    /// the <see cref="SerializationData"/> to write into and the <see cref="IReadOnlySerializationData"/>
    /// to read from, so each call site holds exactly the buffers it needs — a write-then-read
    /// round-trip needs only a single reusable <see cref="SerializationData"/>, with no contiguous
    /// copy in between.
    ///
    /// <para>Stateful (the reader/writer are reused across calls); main-thread only. Each helper
    /// method leaves the reader/writer idle on success and resets it on failure, so the helper is
    /// safe to reuse after a thrown payload.</para>
    /// </summary>
    public sealed class SerializationHelper
    {
        readonly BinarySerializationReader _reader;
        readonly BinarySerializationWriter _writer;

        public SerializationHelper(SerializerRegistry registry)
        {
            if (registry == null)
                throw new ArgumentNullException(nameof(registry));

            _reader = new BinarySerializationReader(registry);
            _writer = new BinarySerializationWriter(registry);
        }

        /// <summary>
        /// The underlying writer, for multi-field payloads written field-by-field. Drive it directly:
        /// <c>Start(target, …)</c>, one <c>Write*</c> per field, then <c>Complete()</c>; the finished
        /// payload IS <c>target</c>. For a single-value payload prefer <see cref="WriteAll{T}"/>.
        /// </summary>
        public BinarySerializationWriter Writer => _writer;

        /// <summary>
        /// The underlying reader, for multi-field payloads. Drive it directly: <c>Start(source)</c>,
        /// one read per field, then <c>Complete()</c> (or <c>CompletePartial()</c> for a deliberate
        /// peek). For a single-value payload prefer <see cref="ReadAll{T}"/>.
        /// </summary>
        public BinarySerializationReader Reader => _reader;

        /// <summary>
        /// Force the reader and writer back to idle, discarding any in-progress read/write. For
        /// exception recovery around direct <see cref="Reader"/> / <see cref="Writer"/> use — the
        /// <see cref="WriteAll{T}"/> / <see cref="ReadAll{T}"/> helpers already self-reset on throw.
        /// </summary>
        public void ResetForErrorRecovery()
        {
            _reader.ResetForErrorRecovery();
            _writer.ResetForErrorRecovery();
        }

        /// <summary>
        /// Serialize <paramref name="value"/> as the sole payload of <paramref name="target"/>. The
        /// target is cleared first (by the writer), so the caller can reuse one instance across
        /// calls. Read it back with <see cref="ReadAll{T}"/>, checksum it
        /// (<see cref="SerializationData.ComputeContiguousChecksum"/>), or emit its contiguous form
        /// (<see cref="SerializationData.WriteContiguousTo(System.IO.Stream)"/>).
        /// </summary>
        public void WriteAll<T>(
            SerializationData target,
            in T value,
            int version,
            bool includeTypeChecks,
            long flags = 0
        )
        {
            try
            {
                _writer.Start(
                    target,
                    version: version,
                    includeTypeChecks: includeTypeChecks,
                    flags: flags
                );
                _writer.Write("Value", value);
                _writer.Complete();
            }
            catch
            {
                _writer.ResetForErrorRecovery();
                throw;
            }
        }

        /// <summary>
        /// <see cref="WriteAll{T}"/> for a polymorphic (abstract / interface) value: writes the
        /// concrete type id so it can round-trip through <see cref="ReadAllObject"/>.
        /// </summary>
        public void WriteAllObject(
            SerializationData target,
            object value,
            int version,
            bool includeTypeChecks,
            long flags = 0
        )
        {
            try
            {
                _writer.Start(
                    target,
                    version: version,
                    includeTypeChecks: includeTypeChecks,
                    flags: flags
                );
                _writer.WriteObject("Value", value);
                _writer.Complete();
            }
            catch
            {
                _writer.ResetForErrorRecovery();
                throw;
            }
        }

        /// <summary>
        /// Delta counterpart to <see cref="WriteAll{T}"/>: serialize <paramref name="value"/> as a
        /// delta against <paramref name="baseValue"/>. Read it back with <see cref="ReadAllDelta{T}"/>
        /// supplying the same base.
        /// </summary>
        public void WriteAllDelta<T>(
            SerializationData target,
            in T value,
            in T baseValue,
            int version,
            bool includeTypeChecks,
            long flags = 0
        )
        {
            try
            {
                _writer.Start(
                    target,
                    version: version,
                    includeTypeChecks: includeTypeChecks,
                    flags: flags
                );
                _writer.WriteDelta("Value", value, baseValue);
                _writer.Complete();
            }
            catch
            {
                _writer.ResetForErrorRecovery();
                throw;
            }
        }

        /// <summary>
        /// Deserialize the sole payload written by <see cref="WriteAll{T}"/> out of
        /// <paramref name="source"/> — a retained <see cref="SerializationData"/> or a
        /// <see cref="ContiguousSerializationData"/> view over loaded bytes. Verifies the data
        /// section was consumed exactly.
        /// </summary>
        public T ReadAll<T>(IReadOnlySerializationData source)
        {
            try
            {
                _reader.Start(source);
                var result = _reader.Read<T>("Value");
                _reader.Complete();
                return result;
            }
            catch
            {
                _reader.ResetForErrorRecovery();
                throw;
            }
        }

        /// <summary>
        /// <see cref="ReadAll{T}"/> into a pre-existing <paramref name="value"/> — for types that
        /// deserialize in place (e.g. a caller-owned container that the serializer fills) rather
        /// than returning a fresh instance.
        /// </summary>
        public void ReadAll<T>(IReadOnlySerializationData source, ref T value)
        {
            try
            {
                _reader.Start(source);
                _reader.Read<T>("Value", ref value);
                _reader.Complete();
            }
            catch
            {
                _reader.ResetForErrorRecovery();
                throw;
            }
        }

        /// <summary>
        /// Read back a payload written by <see cref="WriteAllDelta{T}"/>, reconstructing the value
        /// from the same <paramref name="baseValue"/> the delta was written against.
        /// </summary>
        public T ReadAllDelta<T>(IReadOnlySerializationData source, in T baseValue)
        {
            try
            {
                _reader.Start(source);
                var result = _reader.ReadDelta<T>("Value", baseValue);
                _reader.Complete();
                return result;
            }
            catch
            {
                _reader.ResetForErrorRecovery();
                throw;
            }
        }

        /// <summary>
        /// <see cref="ReadAll{T}"/> for a polymorphic value written with <see cref="WriteAllObject"/>.
        /// </summary>
        public object ReadAllObject(IReadOnlySerializationData source)
        {
            try
            {
                _reader.Start(source);
                var result = _reader.ReadObject("Value");
                _reader.Complete();
                return result;
            }
            catch
            {
                _reader.ResetForErrorRecovery();
                throw;
            }
        }
    }
}
