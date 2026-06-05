using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Trecs.Internal;

namespace Trecs
{
    public sealed class BinarySerializationWriter : ISerializationWriter
    {
        static readonly TrecsLog _log = TrecsLog.Default;

        readonly SerializerRegistry _serializerManager;

        // The writer serializes directly into the two buffers of a caller-supplied
        // SerializationData: the data section (strings, bytes, type ids, blits) and — via
        // _bitWriter — the packed bit-field section. _active points at the instance the current
        // write targets (the one passed to Start) and is null when no write is in progress;
        // Complete releases it so the writer owns no buffer and holds no reference to a now
        // caller-owned (potentially retained) instance. The caller reads the finished payload
        // back from the instance it passed to Start.
        SerializationData _active;
        readonly BitWriter _bitWriter = new();

        // The data section ArrayBufferWriter of the currently-active SerializationData. All
        // per-call data writes go through here.
        ArrayBufferWriter<byte> _dataBuffer => _active.DataWriter;
#if TRECS_INTERNAL_CHECKS && DEBUG
        readonly SerializationMemoryTracker _memoryTracker = new();
#endif
        long _flags;
        bool _includeTypeChecks;
        bool _hasStarted = false;
        int _version;

        public BinarySerializationWriter(SerializerRegistry serializerManager)
        {
            _serializerManager = serializerManager;
        }

        public long Flags
        {
            get
            {
                TrecsDebugAssert.That(_hasStarted);
                return _flags;
            }
        }

        public int Version
        {
            get
            {
                TrecsDebugAssert.That(_hasStarted);
                return _version;
            }
        }

        public IBufferWriter<byte> DataBuffer
        {
            get
            {
                TrecsDebugAssert.That(_hasStarted);
                return _dataBuffer;
            }
        }

        public bool HasFlag(long flag)
        {
            TrecsDebugAssert.That(_hasStarted);
            return (_flags & flag) != 0;
        }

        public string GetMemoryReport()
        {
#if TRECS_INTERNAL_CHECKS && DEBUG
            return _memoryTracker.GenerateReport();
#else
            return "Memory tracking is disabled (TRECS_INTERNAL_CHECKS not defined)";
#endif
        }

        /// <summary>
        /// Begin a write that serializes directly into <paramref name="target"/>, so the
        /// finished payload IS <paramref name="target"/> rather than a copy of it. The target is
        /// cleared first. Pair with <see cref="Complete"/>, after which the caller reads the
        /// payload back from <paramref name="target"/> (e.g.
        /// <see cref="SerializationData.WriteContiguousTo(System.IO.Stream)"/>,
        /// <see cref="SerializationData.CopyContiguousTo"/>,
        /// <see cref="SerializationData.ComputeContiguousChecksum"/>) and emits / retains it.
        /// </summary>
        public void Start(
            SerializationData target,
            int version,
            bool includeTypeChecks,
            long flags = 0,
            bool enableMemoryTracking = false
        )
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));

            using var _ = TrecsProfiling.Start("SerializationWriter.Start");

            TrecsDebugAssert.That(!_hasStarted);
            _hasStarted = true;

            var reservedUnused =
                flags & SerializationFlags.ReservedMask & ~SerializationFlags.AllDefinedMask;
            TrecsDebugAssert.That(
                reservedUnused == 0,
                "Flag bits {0:X} are reserved for Trecs and must not be set by app code. Use bits >= SerializationFlags.FirstUserBitIndex ({1}) for app-defined flags.",
                reservedUnused,
                SerializationFlags.FirstUserBitIndex
            );

            _flags = flags;
            _includeTypeChecks = includeTypeChecks;
            _version = version;
            _active = target;
            _active.Clear();
            _bitWriter.Start(_active.BitFieldsWriter);

#if TRECS_INTERNAL_CHECKS && DEBUG
            _memoryTracker.Reset(enableMemoryTracking);
#else
            if (enableMemoryTracking)
            {
                _log.Warning("Memory tracking not enabled in production builds");
            }
#endif
        }

        public void BlitWriteDelta<T>(string name, in T value, in T baseValue)
            where T : unmanaged
        {
            TrecsDebugAssert.That(_hasStarted);

            bool isChanged = !UnmanagedUtil.BlittableEquals(value, baseValue);
            _bitWriter.WriteBit(isChanged);

            if (isChanged)
            {
                MemoryBlitter.Write(value, _dataBuffer);
            }
        }

        public void WriteObjectDelta(string name, object value, object baseValue)
        {
            TrecsDebugAssert.That(_hasStarted);

            // Delta serialization doesn't support nulls yet
            TrecsDebugAssert.IsNotNull(value, "Delta serialization doesn't support null values");
            TrecsDebugAssert.IsNotNull(
                baseValue,
                "Delta serialization doesn't support null base values"
            );

            var concreteValueType = value.GetType();
            TrecsDebugAssert.That(concreteValueType == baseValue.GetType());

#if TRECS_INTERNAL_CHECKS && DEBUG
            long startPos = _dataBuffer.WrittenCount;
            _memoryTracker.BeginTrackingField(name, concreteValueType);
#endif

#if TRECS_INTERNAL_CHECKS && DEBUG
            TrecsDebugAssert.That(
                concreteValueType.DerivesFrom(
                    typeof(IEquatable<>).MakeGenericType(concreteValueType)
                )
            );
#endif
#if TRECS_INTERNAL_CHECKS && DEBUG
            long typeIdStartPos = _dataBuffer.WrittenCount;
#endif
            TypeIdSerializer.Write(concreteValueType, _dataBuffer);
#if TRECS_INTERNAL_CHECKS && DEBUG
            _memoryTracker.TrackTypeId(
                concreteValueType,
                (int)(_dataBuffer.WrittenCount - typeIdStartPos)
            );
#endif

            // Note that we don't do equality checks here, so it's up to
            // the serializer to do delta optimization logic
            var serializer = _serializerManager.GetSerializerDelta(concreteValueType);
            serializer.SerializeObjectDelta(value, baseValue, this);

#if TRECS_INTERNAL_CHECKS && DEBUG
            _memoryTracker.EndTrackingField((int)(_dataBuffer.WrittenCount - startPos));
#endif
        }

        public void WriteBit(bool value)
        {
            TrecsDebugAssert.That(_hasStarted);

            _bitWriter.WriteBit(value);
        }

        // Advantages of Write<T> over WriteObject (apply only when T is non-abstract;
        // abstract T is auto-forwarded to the WriteObject path):
        // - Avoids boxing; the value is passed by reference down to the serializer.
        // - The type id is not part of the payload. T is statically known on
        //   read, so the type id is only emitted (when _includeTypeChecks is on)
        //   as a verification stamp, not as data the reader needs. WriteObject
        //   must always emit it so the reader knows which concrete type to
        //   instantiate.
        public void WriteDelta<T>(string name, in T value, in T baseValue)
        {
            TrecsDebugAssert.That(_hasStarted);

            // Delta serialization doesn't support nulls yet
            TrecsDebugAssert.That(value != null, "Delta serialization doesn't support null values");
            TrecsDebugAssert.That(
                baseValue != null,
                "Delta serialization doesn't support null base values"
            );

            var type = typeof(T);

            // Abstract base types must use the WriteObjectDelta path to emit the
            // runtime type id; divert automatically so callers don't have to.
            if (type.IsAbstract && type != typeof(Type))
            {
                WriteObjectDelta(name, value, baseValue);
                return;
            }

            TrecsDebugAssert.That(
                type == typeof(Type) || type.IsValueType || value.GetType() == type,
                "WriteDelta<T> requires the runtime type to match T={0} exactly; use WriteObjectDelta for polymorphic types",
                type
            );
            TrecsDebugAssert.That(
                type == typeof(Type) || type.IsValueType || baseValue.GetType() == type,
                "WriteDelta<T> requires baseValue runtime type to match T={0} exactly",
                type
            );

#if TRECS_INTERNAL_CHECKS && DEBUG
            long startPos = _dataBuffer.WrittenCount;
            long typeIdBytes = 0;
#endif

            if (_includeTypeChecks)
            {
#if TRECS_INTERNAL_CHECKS && DEBUG
                long typeIdStartPos = _dataBuffer.WrittenCount;
#endif
                TypeIdSerializer.Write(type, _dataBuffer);
#if TRECS_INTERNAL_CHECKS && DEBUG
                typeIdBytes = _dataBuffer.WrittenCount - typeIdStartPos;
#endif
            }

            // Note that we don't do equality checks here, so it's up to
            // the serializer to do delta optimization logic
            var serializer = _serializerManager.GetSerializerDelta<T>();

#if TRECS_INTERNAL_CHECKS && DEBUG
            long dataStartPos = _dataBuffer.WrittenCount;
            _memoryTracker.BeginTrackingField(name, type);
#endif

            serializer.SerializeDelta(value, baseValue, this);

#if TRECS_INTERNAL_CHECKS && DEBUG
            long totalBytesWritten = _dataBuffer.WrittenCount - dataStartPos;
            _memoryTracker.TrackTypeId(type, (int)typeIdBytes);
            _memoryTracker.EndTrackingField((int)totalBytesWritten);
#endif
        }

        public void WriteStringDelta(string name, string value, string baseValue)
        {
            TrecsDebugAssert.That(_hasStarted);

            // Delta serialization doesn't support nulls yet
            TrecsDebugAssert.IsNotNull(value, "Delta serialization doesn't support null values");
            TrecsDebugAssert.IsNotNull(
                baseValue,
                "Delta serialization doesn't support null base values"
            );

            bool isChanged = value != baseValue;
            _bitWriter.WriteBit(isChanged);

            if (isChanged)
            {
                WriteStringToBuffer(value);
            }
        }

        /// <summary>
        /// Finalize the write: flush the trailing partial bit byte and stamp the header fields
        /// onto the target <see cref="SerializationData"/>. Performs no copy — the payload was
        /// written straight into the target. Afterward, read the finished payload back from the
        /// <see cref="SerializationData"/> you passed to
        /// <see cref="Start(SerializationData, int, bool, long, bool)"/> and emit / retain /
        /// checksum it however you like (e.g.
        /// <see cref="SerializationData.WriteContiguousTo(System.IO.Stream)"/>,
        /// <see cref="SerializationData.CopyContiguousTo"/>,
        /// <see cref="SerializationData.ComputeContiguousChecksum"/>).
        /// </summary>
        public void Complete()
        {
            TrecsDebugAssert.That(_hasStarted);
            _hasStarted = false;

            _bitWriter.Complete();
            _active.Version = _version;
            _active.Flags = _flags;
            _active.IncludeTypeChecks = _includeTypeChecks;
            _active.BitFieldBitCount = _bitWriter.BitCount;

            // The header / bit-field-prefix / sentinel bytes are synthesized only when the
            // contiguous form is emitted (they are not stored in the section buffers), so record
            // their sizes here to keep the memory report's overhead accounting complete. The
            // per-field data bytes are already tracked during Write*.
#if TRECS_INTERNAL_CHECKS && DEBUG
            _memoryTracker.TrackHeaderBytes(SerializationHeaderUtil.Size, "Version/Flags");
            _memoryTracker.TrackHeaderBytes(
                sizeof(int) * 2 + _active.BitFieldBytes.Length,
                "BitFields"
            );
            _memoryTracker.TrackHeaderBytes(1, "Sentinel");
#endif

            // Release the target: the writer owns no buffer and must hold no reference to a now
            // caller-owned (potentially retained) instance that a later ResetForErrorRecovery
            // could clear out from under the caller.
            _active = null;
        }

        public void ResetForErrorRecovery()
        {
            _hasStarted = false;
            _flags = 0;
            _version = 0;
            _includeTypeChecks = false;
            // Clears both the data and bit-field sections of the active instance (the owned
            // default, or a caller's target which it will then discard/despawn).
            _active?.Clear();
            _bitWriter.ResetForErrorRecovery();
        }

        void WriteStringToBuffer(string value)
        {
            int byteCount = Encoding.UTF8.GetByteCount(value);

            var countSpan = _dataBuffer.GetSpan(sizeof(int));
            BinaryPrimitives.WriteInt32LittleEndian(countSpan, byteCount);
            _dataBuffer.Advance(sizeof(int));

            if (byteCount > 0)
            {
                var span = _dataBuffer.GetSpan(byteCount);
                int written = Encoding.UTF8.GetBytes(value, span);
                TrecsDebugAssert.That(
                    written == byteCount,
                    "UTF-8 encode mismatch: expected {0} bytes, wrote {1}",
                    byteCount,
                    written
                );
                _dataBuffer.Advance(byteCount);
            }
        }

        public long NumBytesWritten
        {
            get
            {
                TrecsDebugAssert.That(_hasStarted);
                return _dataBuffer.WrittenCount + ((_bitWriter.BitCount + 7) / 8);
            }
        }

        public void BlitWrite<T>(string name, in T value)
            where T : unmanaged
        {
            TrecsDebugAssert.That(_hasStarted);

#if TRECS_INTERNAL_CHECKS && DEBUG
            long startPos = _dataBuffer.WrittenCount;
#endif
            MemoryBlitter.Write(value, _dataBuffer);
#if TRECS_INTERNAL_CHECKS && DEBUG
            _memoryTracker.TrackDirectWrite(
                name,
                typeof(T),
                (int)(_dataBuffer.WrittenCount - startPos)
            );
#endif
        }

        public unsafe void BlitWriteArrayPtr<T>(string name, T* value, int length)
            where T : unmanaged
        {
            TrecsDebugAssert.That(_hasStarted);

            MemoryBlitter.WriteArrayPtr(value, length, _dataBuffer);
        }

        public void WriteTypeId(string name, Type type)
        {
            TrecsDebugAssert.That(_hasStarted);

            TypeIdSerializer.Write(type, _dataBuffer);
        }

        public void WriteObject(string name, object value)
        {
            TrecsDebugAssert.That(_hasStarted);

            // Write null bit
            bool isNull = value == null;
            _bitWriter.WriteBit(isNull);

            if (isNull)
            {
                return;
            }

            var type = value.GetType();

#if TRECS_INTERNAL_CHECKS && DEBUG
            long startPos = _dataBuffer.WrittenCount;
            _memoryTracker.BeginTrackingField(name, type);
#endif

#if TRECS_INTERNAL_CHECKS && DEBUG
            long typeIdStartPos = _dataBuffer.WrittenCount;
#endif
            TypeIdSerializer.Write(type, _dataBuffer);
#if TRECS_INTERNAL_CHECKS && DEBUG
            _memoryTracker.TrackTypeId(type, (int)(_dataBuffer.WrittenCount - typeIdStartPos));
#endif

            var serializer = _serializerManager.GetSerializer(type);
            serializer.SerializeObject(value, this);

#if TRECS_INTERNAL_CHECKS && DEBUG
            _memoryTracker.EndTrackingField((int)(_dataBuffer.WrittenCount - startPos));
#endif
        }

        public unsafe void BlitWriteArray<T>(string name, T[] value, int count)
            where T : unmanaged
        {
            TrecsDebugAssert.That(_hasStarted);

            MemoryBlitter.WriteArray(value, count, _dataBuffer);
        }

        // Advantages of Write<T> over WriteObject (apply only when T is non-abstract;
        // abstract T is auto-forwarded to the WriteObject path):
        // - Avoids boxing; the value is passed by reference down to the serializer.
        // - The type id is not part of the payload. T is statically known on
        //   read, so the type id is only emitted (when _includeTypeChecks is on)
        //   as a verification stamp, not as data the reader needs. WriteObject
        //   must always emit it so the reader knows which concrete type to
        //   instantiate.
        public void Write<T>(string name, in T value)
        {
            TrecsDebugAssert.That(_hasStarted);

            var type = typeof(T);

            // Abstract base types must use the WriteObject path to emit the runtime
            // type id; divert automatically so callers don't have to.
            if (type.IsAbstract && type != typeof(Type))
            {
                WriteObject(name, value);
                return;
            }

            // For value types, null checking doesn't apply
            if (!type.IsValueType)
            {
                // Write null bit
                bool isNull = value == null;
                _bitWriter.WriteBit(isNull);

                if (isNull)
                {
                    return;
                }
            }

            TrecsDebugAssert.That(
                type == typeof(Type) || type.IsValueType || value.GetType() == type,
                "Write<T> requires the runtime type to match T={0} exactly; use WriteObject for polymorphic types",
                type
            );

#if TRECS_INTERNAL_CHECKS && DEBUG
            long startPos = _dataBuffer.WrittenCount;
            _memoryTracker.BeginTrackingField(name, type);
#endif

            if (_includeTypeChecks)
            {
#if TRECS_INTERNAL_CHECKS && DEBUG
                long typeIdStartPos = _dataBuffer.WrittenCount;
#endif
                TypeIdSerializer.Write(type, _dataBuffer);
#if TRECS_INTERNAL_CHECKS && DEBUG
                _memoryTracker.TrackTypeId(type, (int)(_dataBuffer.WrittenCount - typeIdStartPos));
#endif
            }

            var serializer = _serializerManager.GetSerializer<T>();
            serializer.Serialize(value, this);

#if TRECS_INTERNAL_CHECKS && DEBUG
            _memoryTracker.EndTrackingField((int)(_dataBuffer.WrittenCount - startPos));
#endif
        }

        public void WriteString(string name, string value)
        {
            TrecsDebugAssert.That(_hasStarted);

            // Write null bit
            bool isNull = value == null;
            _bitWriter.WriteBit(isNull);

            if (isNull)
            {
                return;
            }

#if TRECS_INTERNAL_CHECKS && DEBUG
            long startPos = _dataBuffer.WrittenCount;
#endif
            WriteStringToBuffer(value);
#if TRECS_INTERNAL_CHECKS && DEBUG
            _memoryTracker.TrackDirectWrite(
                name,
                typeof(string),
                (int)(_dataBuffer.WrittenCount - startPos)
            );
#endif
        }

        public unsafe void BlitWriteRawBytes(string name, void* ptr, int numBytes)
        {
            TrecsDebugAssert.That(_hasStarted);
            MemoryBlitter.WriteRaw(ptr, numBytes, _dataBuffer);
        }

        public void WriteBytes(string name, byte[] buffer, int offset, int count)
        {
            TrecsDebugAssert.That(_hasStarted);
#if TRECS_INTERNAL_CHECKS && DEBUG
            long startPos = _dataBuffer.WrittenCount;
#endif
            var countSpan = _dataBuffer.GetSpan(sizeof(int));
            BinaryPrimitives.WriteInt32LittleEndian(countSpan, count);
            _dataBuffer.Advance(sizeof(int));

            if (count > 0)
            {
                var dest = _dataBuffer.GetSpan(count);
                new ReadOnlySpan<byte>(buffer, offset, count).CopyTo(dest);
                _dataBuffer.Advance(count);
            }
#if TRECS_INTERNAL_CHECKS && DEBUG
            _memoryTracker.TrackDirectWrite(
                name,
                typeof(byte[]),
                (int)(_dataBuffer.WrittenCount - startPos)
            );
#endif
        }
    }
}
