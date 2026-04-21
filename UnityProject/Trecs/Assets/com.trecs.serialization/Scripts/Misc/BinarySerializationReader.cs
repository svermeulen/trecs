using System;
using System.IO;
using Trecs.Internal;

namespace Trecs.Serialization
{
    public class BinarySerializationReader : ISerializationReader
    {
        static readonly TrecsLog _log = new(nameof(BinarySerializationReader));

        readonly SerializerRegistry _serializerManager;
        readonly BitReader _bitReader = new();

        long _flags;
        int _version;
        bool _includesTypeChecks;
        BinaryReader _binaryReader;
        bool _hasStarted;

        public BinarySerializationReader(SerializerRegistry serializerManager)
        {
            _serializerManager = serializerManager;
        }

        public long Flags
        {
            get
            {
                Assert.That(_hasStarted);
                return _flags;
            }
        }

        public BinaryReader BinaryReader
        {
            get
            {
                Assert.That(_hasStarted);
                Assert.IsNotNull(_binaryReader);
                return _binaryReader;
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

        public bool HasFlag(long flag)
        {
            Assert.That(_hasStarted);
            return (_flags & flag) != 0;
        }

        /// <summary>
        /// Begin reading from <paramref name="binaryReader"/>. The version and
        /// flags are read from the payload header — they're not caller-supplied
        /// — and become accessible via <see cref="Version"/> / <see cref="Flags"/>.
        /// </summary>
        public void Start(BinaryReader binaryReader)
        {
            Assert.That(!_hasStarted);

            _hasStarted = true;

            Assert.IsNull(_binaryReader);
            _binaryReader = binaryReader;

            (_version, _flags, _includesTypeChecks) = SerializationHeaderUtil.ReadHeader(
                binaryReader
            );

            _log.Trace("Completed reading header at byte {}", binaryReader.BaseStream.Position);

            var positionBefore = binaryReader.BaseStream.Position;
            _bitReader.Reset(binaryReader);
            _log.Trace(
                "Bit fields read in {} bytes",
                binaryReader.BaseStream.Position - positionBefore
            );
        }

        public void Stop(bool verifySentinel)
        {
            Assert.That(_hasStarted);
            _hasStarted = false;
            _bitReader.Complete();

            Assert.IsNotNull(_binaryReader);
            if (verifySentinel)
            {
                VerifySentinel();
            }
            _binaryReader = null;
        }

        public void ResetForErrorRecovery()
        {
            _hasStarted = false;
            _binaryReader = null;
            _flags = 0;
            _bitReader.ResetForErrorRecovery();
        }

        void VerifySentinel()
        {
            try
            {
                var sentinel = _binaryReader.ReadByte();
                Assert.That(
                    sentinel == SerializationConstants.SentinelValue,
                    "Data corruption detected - expected sentinel value {} but found {}. This indicates incomplete deserialization, corrupted data, or version mismatch.",
                    SerializationConstants.SentinelValue,
                    sentinel
                );
                _log.Trace("Verified sentinel value at end of stream");
            }
            catch (EndOfStreamException)
            {
                throw Assert.CreateException(
                    "Data corruption detected - missing sentinel value at end of stream. This indicates truncated data or incomplete serialization."
                );
            }
        }

        public bool ReadBit()
        {
            Assert.That(_hasStarted);
            return _bitReader.ReadBit();
        }

        public void ReadObjectDelta(string name, ref object value, object baseValue)
        {
            Assert.That(_hasStarted);

            // Delta serialization doesn't support nulls yet
            Assert.IsNotNull(baseValue, "Delta serialization doesn't support null base values");

            var concreteType = _serializerManager.ReadTypeId(_binaryReader);

            // Note that we don't do equality checks here, so it's up to
            // the serializer to do delta optimization logic

            using (TrecsProfiling.Start("Deserializing {}", concreteType))
            {
                var serializer = _serializerManager.GetSerializerDelta(concreteType);
                serializer.DeserializeObjectDelta(ref value, baseValue, this);
                Assert.IsNotNull(value, "Delta serialization doesn't support null values");
                Assert.That(value.GetType() == concreteType);
                Assert.That(baseValue.GetType() == concreteType);
            }
        }

        public void BlitReadDelta<T>(string name, ref T value, in T baseValue)
            where T : unmanaged
        {
            Assert.That(_hasStarted);

            var isChanged = _bitReader.ReadBit();

            if (isChanged)
            {
                MemoryBlitter.Read(ref value, _binaryReader);
            }
            else
            {
                value = baseValue;
            }
        }

        public void ReadDelta<T>(string name, ref T value, in T baseValue)
        {
            Assert.That(_hasStarted);

            // Delta serialization doesn't support nulls yet
            Assert.That(baseValue != null, "Delta serialization doesn't support null base values");

            var type = typeof(T);

            // We need the using code to be explicit when reading/writing an abstract type, because
            // in this case we need to serialize out the type id
            Assert.That(
                !type.IsAbstract || type == typeof(Type),
                "When deserializing from base class/interface, use ReadObject instead"
            );

            if (_includesTypeChecks)
            {
                // a potential optimization here is to just skip the type id in release mode
                // since it's only needed for verification purposes
                var savedType = _serializerManager.ReadTypeId(_binaryReader);
                Assert.That(savedType == type, "Expected type {} but found '{}'", type, savedType);
            }

            // Note that we don't do equality checks here, so it's up to
            // the serializer to do delta optimization logic

            using (TrecsProfiling.Start("Deserializing {}", type))
            {
                var serializer = _serializerManager.GetSerializerDelta<T>();
                serializer.DeserializeDelta(ref value, baseValue, this);
            }
        }

        public string ReadStringDelta(string name, string baseValue)
        {
            Assert.That(_hasStarted);

            // Delta serialization doesn't support nulls yet
            Assert.IsNotNull(baseValue, "Delta serialization doesn't support null base values");

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
            Assert.That(_hasStarted);

            return _serializerManager.ReadTypeId(_binaryReader);
        }

        public void ReadObject(string name, ref object value)
        {
            Assert.That(_hasStarted);

            // Read null bit
            bool isNull = _bitReader.ReadBit();
            if (isNull)
            {
                value = null;
                return;
            }

            var concreteType = _serializerManager.ReadTypeId(_binaryReader);

            using (TrecsProfiling.Start("Deserializing {}", concreteType))
            {
                var serializer = _serializerManager.GetSerializer(concreteType);
                serializer.DeserializeObject(ref value, this);
                Assert.IsNotNull(value);
                Assert.That(value.GetType() == concreteType);
            }
        }

        public void BlitRead<T>(string name, ref T value)
            where T : unmanaged
        {
            Assert.That(_hasStarted);
            MemoryBlitter.Read(ref value, _binaryReader);
        }

        public unsafe void BlitReadArrayPtr<T>(string name, T* value, int length)
            where T : unmanaged
        {
            Assert.That(_hasStarted);
            MemoryBlitter.ReadArrayPtr(value, length, _binaryReader);
        }

        public unsafe void BlitReadArray<T>(string name, T[] buffer, int count)
            where T : unmanaged
        {
            Assert.That(_hasStarted);
            MemoryBlitter.ReadArray(buffer, count, _binaryReader);
        }

        public void Read<T>(string name, ref T value)
        {
            Assert.That(_hasStarted);

            var type = typeof(T);

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

            // We need the using code to be explicit when reading/writing an abstract type, because
            // in this case we need to serialize out the type id
            Assert.That(
                !type.IsAbstract || type == typeof(Type),
                "When deserializing from base class/interface, use ReadObject instead"
            );

            if (_includesTypeChecks)
            {
                var savedType = _serializerManager.ReadTypeId(_binaryReader);
                Assert.That(savedType == type, "Expected type {} but found '{}'", type, savedType);
            }

            using (TrecsProfiling.Start("Deserializing {}", type))
            {
                var serializer = _serializerManager.GetSerializer<T>();
                serializer.Deserialize(ref value, this);
            }
        }

        public string ReadString(string name)
        {
            Assert.That(_hasStarted);

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
            Assert.That(_hasStarted);
            MemoryBlitter.ReadRaw(ptr, numBytes, _binaryReader);
        }

        public int ReadBytes(string name, ref byte[] buffer)
        {
            Assert.That(_hasStarted);

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
