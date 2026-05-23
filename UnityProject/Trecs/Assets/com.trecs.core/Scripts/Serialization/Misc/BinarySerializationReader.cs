using System;
using System.IO;
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
        BinaryReader _binaryReader;

        // MemoryStream extracted from _binaryReader.BaseStream at Start
        // time so the blit path can read directly from its underlying
        // byte[] without going through BinaryReader.Read → Stream.Read
        // virtual dispatch. Sharing the same underlying MemoryStream means
        // BinaryReader's next ReadString / ReadInt32 picks up at whatever
        // position the blit fast-path left it at.
        MemoryStream _memoryStream;
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

        public BinaryReader BinaryReader
        {
            get
            {
                TrecsDebugAssert.That(_hasStarted);
                TrecsDebugAssert.IsNotNull(_binaryReader);
                return _binaryReader;
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

        /// <summary>
        /// Begin reading from <paramref name="binaryReader"/>. The version and
        /// flags are read from the payload header — they're not caller-supplied
        /// — and become accessible via <see cref="Version"/> / <see cref="Flags"/>.
        /// </summary>
        public void Start(BinaryReader binaryReader)
        {
            TrecsDebugAssert.That(!_hasStarted);

            _hasStarted = true;

            TrecsDebugAssert.IsNull(_binaryReader);
            _binaryReader = binaryReader;
            // All current callers wrap a MemoryStream — the bulk Heap
            // serialization paths in particular depend on the seek-by-
            // Position behaviour that file-backed streams cost a syscall
            // for. If a non-MemoryStream caller appears, route it through
            // a buffering MemoryStream upstream.
            _memoryStream = (MemoryStream)binaryReader.BaseStream;

            (_version, _flags, _includesTypeChecks) = SerializationHeaderUtil.ReadHeader(
                binaryReader
            );

            _log.Trace("Completed reading header at byte {0}", binaryReader.BaseStream.Position);

            var positionBefore = binaryReader.BaseStream.Position;
            _bitReader.Reset(binaryReader);
            _log.Trace(
                "Bit fields read in {0} bytes",
                binaryReader.BaseStream.Position - positionBefore
            );
        }

        /// <summary>
        /// Finish reading. When <paramref name="verifySentinel"/> is true, the
        /// sentinel is read and validated, leaving the underlying stream
        /// positioned just past it. When false, the underlying stream
        /// position is left undefined — callers in this mode (e.g. peek paths)
        /// must reset the stream themselves before any subsequent read.
        /// </summary>
        public void Stop(bool verifySentinel)
        {
            TrecsDebugAssert.That(_hasStarted);
            _hasStarted = false;
            _bitReader.Complete();

            TrecsDebugAssert.IsNotNull(_binaryReader);
            if (verifySentinel)
            {
                VerifySentinel();
            }
            _binaryReader = null;
            _memoryStream = null;
        }

        public void ResetForErrorRecovery()
        {
            _hasStarted = false;
            _binaryReader = null;
            _memoryStream = null;
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
            byte sentinel;
            try
            {
                sentinel = _binaryReader.ReadByte();
            }
            catch (EndOfStreamException)
            {
                throw new SerializationException(
                    "Data corruption detected — missing end-of-payload marker. "
                        + "This indicates truncated data or incomplete serialization."
                );
            }

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

            var concreteType = TypeIdSerializer.Read(_binaryReader);

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
                MemoryBlitter.Read(ref value, _memoryStream);
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
                // a potential optimization here is to just skip the type id in release mode
                // since it's only needed for verification purposes
                var savedType = TypeIdSerializer.Read(_binaryReader);
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
                return _binaryReader.ReadString();
            }
            else
            {
                return baseValue;
            }
        }

        public Type ReadTypeId(string name)
        {
            TrecsDebugAssert.That(_hasStarted);

            return TypeIdSerializer.Read(_binaryReader);
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

            var concreteType = TypeIdSerializer.Read(_binaryReader);

            var serializer = _serializerManager.GetSerializer(concreteType);
            serializer.DeserializeObject(ref value, this);
            TrecsDebugAssert.IsNotNull(value);
            TrecsDebugAssert.That(value.GetType() == concreteType);
        }

        public void BlitRead<T>(string name, ref T value)
            where T : unmanaged
        {
            TrecsDebugAssert.That(_hasStarted);
            MemoryBlitter.Read(ref value, _memoryStream);
        }

        public unsafe void BlitReadArrayPtr<T>(string name, T* value, int length)
            where T : unmanaged
        {
            TrecsDebugAssert.That(_hasStarted);
            MemoryBlitter.ReadArrayPtr(value, length, _memoryStream);
        }

        public unsafe void BlitReadArray<T>(string name, T[] buffer, int count)
            where T : unmanaged
        {
            TrecsDebugAssert.That(_hasStarted);
            MemoryBlitter.ReadArray(buffer, count, _memoryStream);
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
                var savedType = TypeIdSerializer.Read(_binaryReader);
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

            return _binaryReader.ReadString();
        }

        public unsafe void BlitReadRawBytes(string name, void* ptr, int numBytes)
        {
            TrecsDebugAssert.That(_hasStarted);
            MemoryBlitter.ReadRaw(ptr, numBytes, _memoryStream);
        }

        public int ReadBytes(string name, ref byte[] buffer)
        {
            TrecsDebugAssert.That(_hasStarted);

            var length = _binaryReader.ReadInt32();

            if (buffer == null || buffer.Length < length)
            {
                buffer = new byte[length];
            }

            if (length > 0)
            {
                _binaryReader.Read(buffer, 0, length);
            }

            return length;
        }
    }
}
