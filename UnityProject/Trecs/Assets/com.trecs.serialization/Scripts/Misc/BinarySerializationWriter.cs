using System;
using System.IO;
using Trecs.Internal;

namespace Trecs.Serialization
{
    public class BinarySerializationWriter : ISerializationWriter, IDisposable
    {
        static readonly TrecsLog _log = new(nameof(BinarySerializationWriter));

        readonly SerializerRegistry _serializerManager;
        readonly MemoryStream _dataBuffer;
        readonly BinaryWriter _dataWriter;
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
            _dataBuffer = new MemoryStream();
            _dataWriter = new BinaryWriter(_dataBuffer);
        }

        public long Flags
        {
            get
            {
                Assert.That(_hasStarted);
                return _flags;
            }
        }

        public int Version
        {
            get
            {
                Assert.That(_hasStarted);
                return _version;
            }
        }

        public BinaryWriter BinaryWriter
        {
            get { return _dataWriter; }
        }

        public bool HasFlag(long flag)
        {
            Assert.That(_hasStarted);
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
            using var _ = TrecsProfiling.Start("BinarySerializationWriter.Start");

            Assert.That(!_hasStarted);
            _hasStarted = true;

            var reservedUnused =
                flags & SerializationFlags.ReservedMask & ~SerializationFlags.AllDefinedMask;
            Assert.That(
                reservedUnused == 0,
                "Flag bits {:X} are reserved for Trecs and must not be set by app code. Use bits >= SerializationFlags.FirstUserBitIndex ({}) for app-defined flags.",
                reservedUnused,
                SerializationFlags.FirstUserBitIndex
            );

            _flags = flags;
            _includeTypeChecks = includeTypeChecks;
            _version = version;
            _dataBuffer.Position = 0;
            _dataBuffer.SetLength(0);
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
            Assert.That(_hasStarted);

            bool isChanged = !UnmanagedUtil.BlittableEquals(value, baseValue);
            _bitWriter.WriteBit(isChanged);

            if (isChanged)
            {
                MemoryBlitter.Write(value, _dataWriter);
            }
        }

        public void WriteObjectDelta(string name, object value, object baseValue)
        {
            Assert.That(_hasStarted);

            // Delta serialization doesn't support nulls yet
            Assert.IsNotNull(value, "Delta serialization doesn't support null values");
            Assert.IsNotNull(baseValue, "Delta serialization doesn't support null base values");

            var concreteValueType = value.GetType();
            Assert.That(concreteValueType == baseValue.GetType());

#if TRECS_INTERNAL_CHECKS && DEBUG
            long startPos = _dataBuffer.Position;
            _memoryTracker.BeginTrackingField(name, concreteValueType);
#endif

#if TRECS_INTERNAL_CHECKS && DEBUG
            Assert.That(
                concreteValueType.DerivesFrom(
                    typeof(IEquatable<>).MakeGenericType(concreteValueType)
                )
            );
#endif
            using (TrecsProfiling.Start("WriteTypeId({})", concreteValueType))
            {
#if TRECS_INTERNAL_CHECKS && DEBUG
                long typeIdStartPos = _dataBuffer.Position;
#endif
                _serializerManager.WriteTypeId(concreteValueType, _dataWriter);
#if TRECS_INTERNAL_CHECKS && DEBUG
                _memoryTracker.TrackTypeId(
                    concreteValueType,
                    (int)(_dataBuffer.Position - typeIdStartPos)
                );
#endif
            }

            // Note that we don't do equality checks here, so it's up to
            // the serializer to do delta optimization logic
            using (TrecsProfiling.Start("Serializing {}", concreteValueType))
            {
                var serializer = _serializerManager.GetSerializerDelta(concreteValueType);
                serializer.SerializeObjectDelta(value, baseValue, this);
            }

#if TRECS_INTERNAL_CHECKS && DEBUG
            _memoryTracker.EndTrackingField((int)(_dataBuffer.Position - startPos));
#endif
        }

        public void WriteBit(bool value)
        {
            Assert.That(_hasStarted);

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
            Assert.That(_hasStarted);

            // Delta serialization doesn't support nulls yet
            Assert.That(value != null, "Delta serialization doesn't support null values");
            Assert.That(baseValue != null, "Delta serialization doesn't support null base values");

            var type = typeof(T);

            // Abstract base types must use the WriteObjectDelta path to emit the
            // runtime type id; divert automatically so callers don't have to.
            if (type.IsAbstract && type != typeof(Type))
            {
                WriteObjectDelta(name, value, baseValue);
                return;
            }

            Assert.That(
                type == typeof(Type) || type.IsValueType || value.GetType() == type,
                "WriteDelta<T> requires the runtime type to match T={} exactly; use WriteObjectDelta for polymorphic types",
                type
            );
            Assert.That(
                type == typeof(Type) || type.IsValueType || baseValue.GetType() == type,
                "WriteDelta<T> requires baseValue runtime type to match T={} exactly",
                type
            );

#if TRECS_INTERNAL_CHECKS && DEBUG
            long startPos = _dataBuffer.Position;
            long typeIdBytes = 0;
#endif

            if (_includeTypeChecks)
            {
                using (TrecsProfiling.Start("WriteTypeId({})", type))
                {
#if TRECS_INTERNAL_CHECKS && DEBUG
                    long typeIdStartPos = _dataBuffer.Position;
#endif
                    _serializerManager.WriteTypeId(type, _dataWriter);
#if TRECS_INTERNAL_CHECKS && DEBUG
                    typeIdBytes = _dataBuffer.Position - typeIdStartPos;
#endif
                }
            }

            // Note that we don't do equality checks here, so it's up to
            // the serializer to do delta optimization logic
            ISerializerDelta<T> serializer;

            using (TrecsProfiling.Start("Looking up serializer for type {}", type))
            {
                serializer = _serializerManager.GetSerializerDelta<T>();
            }

#if TRECS_INTERNAL_CHECKS && DEBUG
            long dataStartPos = _dataBuffer.Position;
            _memoryTracker.BeginTrackingField(name, type);
#endif

            using (TrecsProfiling.Start("{}.Serialize", serializer.GetType()))
            {
                serializer.SerializeDelta(value, baseValue, this);
            }

#if TRECS_INTERNAL_CHECKS && DEBUG
            long totalBytesWritten = _dataBuffer.Position - dataStartPos;
            _memoryTracker.TrackTypeId(type, (int)typeIdBytes);
            _memoryTracker.EndTrackingField((int)totalBytesWritten);
#endif
        }

        public void WriteStringDelta(string name, string value, string baseValue)
        {
            Assert.That(_hasStarted);

            // Delta serialization doesn't support nulls yet
            Assert.IsNotNull(value, "Delta serialization doesn't support null values");
            Assert.IsNotNull(baseValue, "Delta serialization doesn't support null base values");

            bool isChanged = value != baseValue;
            _bitWriter.WriteBit(isChanged);

            if (isChanged)
            {
                _dataWriter.Write(value);
            }
        }

        public void Complete(BinaryWriter outputWriter)
        {
            Assert.That(_hasStarted);
            _hasStarted = false;

            // Track header bytes
#if TRECS_INTERNAL_CHECKS && DEBUG
            long headerStartPos = outputWriter.BaseStream.Position;
#endif
            SerializationHeaderUtil.WriteHeader(outputWriter, _version, _flags, _includeTypeChecks);
#if TRECS_INTERNAL_CHECKS && DEBUG
            _memoryTracker.TrackHeaderBytes(
                (int)(outputWriter.BaseStream.Position - headerStartPos),
                "Version/Flags"
            );
#endif

            _log.Trace("Writing bit fields starting at byte {}", outputWriter.BaseStream.Position);
#if TRECS_INTERNAL_CHECKS && DEBUG
            long bitFieldStartPos = outputWriter.BaseStream.Position;
#endif
            _bitWriter.Complete(outputWriter);
#if TRECS_INTERNAL_CHECKS && DEBUG
            _memoryTracker.TrackHeaderBytes(
                (int)(outputWriter.BaseStream.Position - bitFieldStartPos),
                "BitFields"
            );
#endif
            _log.Trace("Completed writing bit fields at byte {}", outputWriter.BaseStream.Position);

            _dataBuffer.Position = 0;
            _dataBuffer.CopyTo(outputWriter.BaseStream);
            _log.Trace(
                "Completed writing data buffer at byte {}",
                outputWriter.BaseStream.Position
            );

            // Write sentinel value to mark end of valid data and detect stream corruption
#if TRECS_INTERNAL_CHECKS && DEBUG
            long sentinelStartPos = outputWriter.BaseStream.Position;
#endif
            outputWriter.Write(SerializationConstants.EndOfPayloadMarker);
#if TRECS_INTERNAL_CHECKS && DEBUG
            _memoryTracker.TrackHeaderBytes(
                (int)(outputWriter.BaseStream.Position - sentinelStartPos),
                "Sentinel"
            );
#endif
            _log.Trace("Wrote sentinel value at byte {}", outputWriter.BaseStream.Position);
        }

        public void ResetForErrorRecovery()
        {
            _hasStarted = false;
            _flags = 0;
            _version = 0;
            _includeTypeChecks = false;
            _dataBuffer.Position = 0;
            _dataBuffer.SetLength(0);
            _bitWriter.ResetForErrorRecovery();
        }

        public void Dispose()
        {
            _dataWriter?.Dispose();
            _dataBuffer?.Dispose();
        }

        public long NumBytesWritten
        {
            get
            {
                Assert.That(_hasStarted);
                return _dataBuffer.Position + ((_bitWriter.BitCount + 7) / 8);
            }
        }

        public void BlitWrite<T>(string name, in T value)
            where T : unmanaged
        {
            Assert.That(_hasStarted);

#if TRECS_INTERNAL_CHECKS && DEBUG
            long startPos = _dataBuffer.Position;
#endif
            MemoryBlitter.Write(value, _dataWriter);
#if TRECS_INTERNAL_CHECKS && DEBUG
            _memoryTracker.TrackDirectWrite(
                name,
                typeof(T),
                (int)(_dataBuffer.Position - startPos)
            );
#endif
        }

        public unsafe void BlitWriteArrayPtr<T>(string name, T* value, int length)
            where T : unmanaged
        {
            Assert.That(_hasStarted);

            MemoryBlitter.WriteArrayPtr(value, length, _dataWriter);
        }

        public void WriteTypeId(string name, Type type)
        {
            Assert.That(_hasStarted);

            _serializerManager.WriteTypeId(type, _dataWriter);
        }

        public void WriteObject(string name, object value)
        {
            Assert.That(_hasStarted);

            // Write null bit
            bool isNull = value == null;
            _bitWriter.WriteBit(isNull);

            if (isNull)
            {
                return;
            }

            var type = value.GetType();

#if TRECS_INTERNAL_CHECKS && DEBUG
            long startPos = _dataBuffer.Position;
            _memoryTracker.BeginTrackingField(name, type);
#endif

            using (TrecsProfiling.Start("WriteTypeId({})", type))
            {
#if TRECS_INTERNAL_CHECKS && DEBUG
                long typeIdStartPos = _dataBuffer.Position;
#endif
                _serializerManager.WriteTypeId(type, _dataWriter);
#if TRECS_INTERNAL_CHECKS && DEBUG
                _memoryTracker.TrackTypeId(type, (int)(_dataBuffer.Position - typeIdStartPos));
#endif
            }

            using (TrecsProfiling.Start("Serializing {}", type))
            {
                var serializer = _serializerManager.GetSerializer(type);
                serializer.SerializeObject(value, this);
            }

#if TRECS_INTERNAL_CHECKS && DEBUG
            _memoryTracker.EndTrackingField((int)(_dataBuffer.Position - startPos));
#endif
        }

        public unsafe void BlitWriteArray<T>(string name, T[] value, int count)
            where T : unmanaged
        {
            Assert.That(_hasStarted);

            MemoryBlitter.WriteArray(value, count, _dataWriter);
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
            Assert.That(_hasStarted);

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

            Assert.That(
                type == typeof(Type) || type.IsValueType || value.GetType() == type,
                "Write<T> requires the runtime type to match T={} exactly; use WriteObject for polymorphic types",
                type
            );

#if TRECS_INTERNAL_CHECKS && DEBUG
            long startPos = _dataBuffer.Position;
            _memoryTracker.BeginTrackingField(name, type);
#endif

            if (_includeTypeChecks)
            {
                using (TrecsProfiling.Start("WriteTypeId({})", type))
                {
#if TRECS_INTERNAL_CHECKS && DEBUG
                    long typeIdStartPos = _dataBuffer.Position;
#endif
                    _serializerManager.WriteTypeId(type, _dataWriter);
#if TRECS_INTERNAL_CHECKS && DEBUG
                    _memoryTracker.TrackTypeId(type, (int)(_dataBuffer.Position - typeIdStartPos));
#endif
                }
            }

            ISerializer<T> serializer;

            using (TrecsProfiling.Start("Looking up serializer for type {}", type))
            {
                serializer = _serializerManager.GetSerializer<T>();
            }

            using (TrecsProfiling.Start("{}.Serialize", serializer.GetType()))
            {
                serializer.Serialize(value, this);
            }

#if TRECS_INTERNAL_CHECKS && DEBUG
            _memoryTracker.EndTrackingField((int)(_dataBuffer.Position - startPos));
#endif
        }

        public void WriteString(string name, string value)
        {
            Assert.That(_hasStarted);

            // Write null bit
            bool isNull = value == null;
            _bitWriter.WriteBit(isNull);

            if (isNull)
            {
                return;
            }

#if TRECS_INTERNAL_CHECKS && DEBUG
            long startPos = _dataBuffer.Position;
#endif
            _dataWriter.Write(value);
#if TRECS_INTERNAL_CHECKS && DEBUG
            _memoryTracker.TrackDirectWrite(
                name,
                typeof(string),
                (int)(_dataBuffer.Position - startPos)
            );
#endif
        }

        public unsafe void BlitWriteRawBytes(string name, void* ptr, int numBytes)
        {
            Assert.That(_hasStarted);
            MemoryBlitter.WriteRaw(ptr, numBytes, _dataWriter);
        }

        public void WriteBytes(string name, byte[] buffer, int offset, int count)
        {
            Assert.That(_hasStarted);
#if TRECS_INTERNAL_CHECKS && DEBUG
            long startPos = _dataBuffer.Position;
#endif
            _dataWriter.Write(count);

            if (count > 0)
            {
                _dataWriter.Write(buffer, offset, count);
            }
#if TRECS_INTERNAL_CHECKS && DEBUG
            _memoryTracker.TrackDirectWrite(
                name,
                typeof(byte[]),
                (int)(_dataBuffer.Position - startPos)
            );
#endif
        }
    }
}
