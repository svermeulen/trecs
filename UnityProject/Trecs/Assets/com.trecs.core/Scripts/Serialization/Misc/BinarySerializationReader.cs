using System;
using System.Text;
using Trecs.Internal;

namespace Trecs
{
    public sealed class BinarySerializationReader : ISerializationReader
    {
        static readonly TrecsLog _log = TrecsLog.Default;

        readonly SerializerRegistry _serializerManager;
        readonly BitReader _bitReader = new();

        long _flags;
        int _version;
        bool _includesTypeChecks;

        ReadOnlyMemory<byte> _data;
        int _offset;
        bool _hasStarted;

        public BinarySerializationReader(SerializerRegistry serializerManager)
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

        public bool HasFlag(long flag)
        {
            TrecsDebugAssert.That(_hasStarted);
            return (_flags & flag) != 0;
        }

        public void Start(ReadOnlyMemory<byte> data)
        {
            TrecsDebugAssert.That(!_hasStarted);

            _hasStarted = true;
            _data = data;
            _offset = 0;

            var span = _data.Span;
            (_version, _flags, _includesTypeChecks) = SerializationHeaderUtil.ReadHeader(
                span,
                ref _offset
            );

            _log.Trace("Completed reading header at offset {0}", _offset);

            var offsetBefore = _offset;
            _bitReader.Reset(span, ref _offset);
            _log.Trace("Bit fields read in {0} bytes", _offset - offsetBefore);
        }

        /// <summary>
        /// Finish reading. When <paramref name="verifySentinel"/> is true, the
        /// sentinel is read and validated. When false, no further reads occur.
        /// </summary>
        public void Stop(bool verifySentinel)
        {
            TrecsDebugAssert.That(_hasStarted);
            _hasStarted = false;
            _bitReader.Complete();

            if (verifySentinel)
            {
                VerifySentinel();
            }
            _data = default;
        }

        public void ResetForErrorRecovery()
        {
            _hasStarted = false;
            _data = default;
            _offset = 0;
            _flags = 0;
            _version = 0;
            _includesTypeChecks = false;
            _bitReader.ResetForErrorRecovery();
        }

        /// <summary>
        /// Verify the payload-level <see cref="SerializationConstants.EndOfPayloadMarker"/>
        /// that <see cref="BinarySerializationWriter"/> appends to every stream.
        /// Distinct from the ECS-state-level guard inside <c>WorldStateSerializer</c>
        /// (<c>WorldStateStreamGuard</c>), which is checked during <c>DeserializeState</c>.
        /// </summary>
        void VerifySentinel()
        {
            // SerializationException (not TrecsDebugAssert) because the end-
            // of-payload sentinel is the primary defense against truncated /
            // corrupt streams and must fire in release builds too. Stripping
            // the check in release would let downstream code silently consume
            // an incomplete payload as if it were valid.
            var span = _data.Span;
            if (_offset >= span.Length)
            {
                throw new SerializationException(
                    "Data corruption detected — missing end-of-payload marker. "
                        + "This indicates truncated data or incomplete serialization."
                );
            }

            byte sentinel = span[_offset];
            _offset++;
            if (sentinel != SerializationConstants.EndOfPayloadMarker)
            {
                throw new SerializationException(
                    $"Data corruption detected — expected end-of-payload marker "
                        + $"0x{SerializationConstants.EndOfPayloadMarker:X2} but found "
                        + $"0x{sentinel:X2}. This indicates incomplete deserialization, "
                        + "corrupted data, or version mismatch."
                );
            }
            _log.Trace("Verified end-of-payload marker");
        }

        public bool ReadBit()
        {
            TrecsDebugAssert.That(_hasStarted);
            return _bitReader.ReadBit();
        }

        public void ReadObjectDelta(string name, ref object value, object baseValue)
        {
            TrecsDebugAssert.That(_hasStarted);

            // Delta serialization doesn't support nulls yet
            TrecsDebugAssert.IsNotNull(
                baseValue,
                "Delta serialization doesn't support null base values"
            );

            var concreteType = TypeIdSerializer.Read(_data.Span, ref _offset);

            // Note that we don't do equality checks here, so it's up to
            // the serializer to do delta optimization logic

            var serializer = _serializerManager.GetSerializerDelta(concreteType);
            serializer.DeserializeObjectDelta(ref value, baseValue, this);
            TrecsDebugAssert.IsNotNull(value, "Delta serialization doesn't support null values");
            TrecsDebugAssert.That(value.GetType() == concreteType);
            TrecsDebugAssert.That(baseValue.GetType() == concreteType);
        }

        public void BlitReadDelta<T>(string name, ref T value, in T baseValue)
            where T : unmanaged
        {
            TrecsDebugAssert.That(_hasStarted);

            var isChanged = _bitReader.ReadBit();

            if (isChanged)
            {
                MemoryBlitter.Read(ref value, _data.Span, ref _offset);
            }
            else
            {
                value = baseValue;
            }
        }

        public void ReadDelta<T>(string name, ref T value, in T baseValue)
        {
            TrecsDebugAssert.That(_hasStarted);

            // Delta serialization doesn't support nulls yet
            TrecsDebugAssert.That(
                baseValue != null,
                "Delta serialization doesn't support null base values"
            );

            var type = typeof(T);

            // Abstract base types must use the ReadObjectDelta path to read the
            // runtime type id; divert automatically so callers don't have to.
            if (type.IsAbstract && type != typeof(Type))
            {
                object obj = value;
                ReadObjectDelta(name, ref obj, baseValue);
                TrecsDebugAssert.That(
                    obj is T,
                    "Wire concrete type {0} does not derive from T={1}",
                    obj?.GetType(),
                    type
                );
                value = (T)obj;
                return;
            }

            if (_includesTypeChecks)
            {
                var savedType = TypeIdSerializer.Read(_data.Span, ref _offset);
                TrecsDebugAssert.That(
                    savedType == type,
                    "Expected type {0} but found '{1}'",
                    type,
                    savedType
                );
            }

            // Note that we don't do equality checks here, so it's up to
            // the serializer to do delta optimization logic

            var serializer = _serializerManager.GetSerializerDelta<T>();
            serializer.DeserializeDelta(ref value, baseValue, this);
        }

        public string ReadStringDelta(string name, string baseValue)
        {
            TrecsDebugAssert.That(_hasStarted);

            // Delta serialization doesn't support nulls yet
            TrecsDebugAssert.IsNotNull(
                baseValue,
                "Delta serialization doesn't support null base values"
            );

            var isChanged = _bitReader.ReadBit();

            if (isChanged)
            {
                return ReadStringFromData();
            }
            else
            {
                return baseValue;
            }
        }

        public Type ReadTypeId(string name)
        {
            TrecsDebugAssert.That(_hasStarted);

            return TypeIdSerializer.Read(_data.Span, ref _offset);
        }

        public void ReadObject(string name, ref object value)
        {
            TrecsDebugAssert.That(_hasStarted);

            // Read null bit
            bool isNull = _bitReader.ReadBit();
            if (isNull)
            {
                value = null;
                return;
            }

            var concreteType = TypeIdSerializer.Read(_data.Span, ref _offset);

            var serializer = _serializerManager.GetSerializer(concreteType);
            serializer.DeserializeObject(ref value, this);
            TrecsDebugAssert.IsNotNull(value);
            TrecsDebugAssert.That(value.GetType() == concreteType);
        }

        public void BlitRead<T>(string name, ref T value)
            where T : unmanaged
        {
            TrecsDebugAssert.That(_hasStarted);
            MemoryBlitter.Read(ref value, _data.Span, ref _offset);
        }

        public unsafe void BlitReadArrayPtr<T>(string name, T* value, int length)
            where T : unmanaged
        {
            TrecsDebugAssert.That(_hasStarted);
            MemoryBlitter.ReadArrayPtr(value, length, _data.Span, ref _offset);
        }

        public unsafe void BlitReadArray<T>(string name, T[] buffer, int count)
            where T : unmanaged
        {
            TrecsDebugAssert.That(_hasStarted);
            MemoryBlitter.ReadArray(buffer, count, _data.Span, ref _offset);
        }

        public void Read<T>(string name, ref T value)
        {
            TrecsDebugAssert.That(_hasStarted);

            var type = typeof(T);

            // Abstract base types must use the ReadObject path to read the runtime
            // type id; divert automatically so callers don't have to.
            if (type.IsAbstract && type != typeof(Type))
            {
                object obj = value;
                ReadObject(name, ref obj);
                TrecsDebugAssert.That(
                    obj == null || obj is T,
                    "Wire concrete type {0} does not derive from T={1}",
                    obj?.GetType(),
                    type
                );
                value = (T)obj;
                return;
            }

            // For value types, null checking doesn't apply
            if (!type.IsValueType)
            {
                // Read null bit
                bool isNull = _bitReader.ReadBit();
                if (isNull)
                {
                    value = default(T);
                    return;
                }
            }

            if (_includesTypeChecks)
            {
                var savedType = TypeIdSerializer.Read(_data.Span, ref _offset);
                TrecsDebugAssert.That(
                    savedType == type,
                    "Expected type {0} but found '{1}'",
                    type,
                    savedType
                );
            }

            var serializer = _serializerManager.GetSerializer<T>();
            serializer.Deserialize(ref value, this);
        }

        public string ReadString(string name)
        {
            TrecsDebugAssert.That(_hasStarted);

            // Read null bit
            bool isNull = _bitReader.ReadBit();
            if (isNull)
            {
                return null;
            }

            return ReadStringFromData();
        }

        string ReadStringFromData()
        {
            int byteCount = 0;
            MemoryBlitter.Read(ref byteCount, _data.Span, ref _offset);

            if (byteCount == 0)
            {
                return string.Empty;
            }

            var span = _data.Span;
            if (_offset + byteCount > span.Length)
            {
                throw new SerializationException(
                    $"Truncated data: expected {byteCount} string bytes at offset {_offset} but data length is only {span.Length}"
                );
            }

            var result = Encoding.UTF8.GetString(span.Slice(_offset, byteCount));
            _offset += byteCount;
            return result;
        }

        public unsafe void BlitReadRawBytes(string name, void* ptr, int numBytes)
        {
            TrecsDebugAssert.That(_hasStarted);
            MemoryBlitter.ReadRaw(ptr, numBytes, _data.Span, ref _offset);
        }

        public int ReadBytes(string name, ref byte[] buffer)
        {
            TrecsDebugAssert.That(_hasStarted);

            int length = 0;
            MemoryBlitter.Read(ref length, _data.Span, ref _offset);

            if (buffer == null || buffer.Length < length)
            {
                buffer = new byte[length];
            }

            if (length > 0)
            {
                var span = _data.Span;
                if (_offset + length > span.Length)
                {
                    throw new SerializationException(
                        $"Truncated data: expected {length} bytes at offset {_offset} but data length is only {span.Length}"
                    );
                }
                span.Slice(_offset, length).CopyTo(buffer);
                _offset += length;
            }

            return length;
        }
    }
}
