using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using Trecs.Internal;

namespace Trecs
{
    public sealed class BinarySerializationWriter : ISerializationWriter
    {
        static readonly TrecsLog _log = TrecsLog.Default;

        // Initial capacity for the data buffer. Sized to keep small payloads
        // (single-frame inputs, header probes) in a single allocation while
        // still letting ArrayBufferWriter geometrically grow for large
        // snapshots without a slow ramp from the default 256-byte start.
        const int InitialDataBufferCapacity = 4 * 1024;

        readonly SerializerRegistry _serializerManager;

        // Data section (post-header, post-bit-fields) is accumulated into an
        // ArrayBufferWriter<byte> so Complete() can flush the bytes via a
        // single span write into the outer stream. All per-call writes
        // (strings, bytes, type ids) go directly into this buffer without
        // a BinaryWriter/Stream intermediary.
        readonly ArrayBufferWriter<byte> _dataBuffer;
        readonly BitWriter _bitWriter = new();
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
            _dataBuffer = new ArrayBufferWriter<byte>(InitialDataBufferCapacity);
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

        public void Start(
            int version,
            bool includeTypeChecks,
            long flags = 0,
            bool enableMemoryTracking = false
        )
        {
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
            _dataBuffer.Clear();
            _bitWriter.Reset();

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

        public void Complete(Stream outputStream)
        {
            TrecsDebugAssert.That(_hasStarted);
            _hasStarted = false;

            // Track header bytes
#if TRECS_INTERNAL_CHECKS && DEBUG
            long headerStartPos = outputStream.Position;
#endif
            SerializationHeaderUtil.WriteHeader(outputStream, _version, _flags, _includeTypeChecks);
#if TRECS_INTERNAL_CHECKS && DEBUG
            _memoryTracker.TrackHeaderBytes(
                (int)(outputStream.Position - headerStartPos),
                "Version/Flags"
            );
#endif

            _log.Trace("Writing bit fields starting at byte {0}", outputStream.Position);
#if TRECS_INTERNAL_CHECKS && DEBUG
            long bitFieldStartPos = outputStream.Position;
#endif
            _bitWriter.Complete(outputStream);
#if TRECS_INTERNAL_CHECKS && DEBUG
            _memoryTracker.TrackHeaderBytes(
                (int)(outputStream.Position - bitFieldStartPos),
                "BitFields"
            );
#endif
            _log.Trace("Completed writing bit fields at byte {0}", outputStream.Position);

            outputStream.Write(_dataBuffer.WrittenSpan);
            _log.Trace("Completed writing data buffer at byte {0}", outputStream.Position);

#if TRECS_INTERNAL_CHECKS && DEBUG
            long sentinelStartPos = outputStream.Position;
#endif
            outputStream.WriteByte(SerializationConstants.EndOfPayloadMarker);
#if TRECS_INTERNAL_CHECKS && DEBUG
            _memoryTracker.TrackHeaderBytes(
                (int)(outputStream.Position - sentinelStartPos),
                "Sentinel"
            );
#endif
            _log.Trace("Wrote sentinel value at byte {0}", outputStream.Position);
        }

        public int ComputeOutputSize()
        {
            TrecsDebugAssert.That(_hasStarted);
            return SerializationHeaderUtil.Size
                + _bitWriter.ComputeOutputSize()
                + _dataBuffer.WrittenCount
                + 1;
        }

        public void CompleteTo(byte[] buffer)
        {
            TrecsDebugAssert.That(_hasStarted);
            _hasStarted = false;

            int offset = 0;
            SerializationHeaderUtil.WriteHeader(
                new Span<byte>(buffer, 0, SerializationHeaderUtil.Size),
                _version,
                _flags,
                _includeTypeChecks
            );
            offset += SerializationHeaderUtil.Size;

            _bitWriter.CompleteTo(buffer, ref offset);

            _dataBuffer.WrittenSpan.CopyTo(
                new Span<byte>(buffer, offset, _dataBuffer.WrittenCount)
            );
            offset += _dataBuffer.WrittenCount;

            buffer[offset] = SerializationConstants.EndOfPayloadMarker;
        }

        public void ResetForErrorRecovery()
        {
            _hasStarted = false;
            _flags = 0;
            _version = 0;
            _includeTypeChecks = false;
            _dataBuffer.Clear();
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
