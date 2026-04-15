using System;
using System.Collections.Generic;
using Trecs.Internal;

namespace Trecs.Serialization
{
    public class EnumSerializer<T> : ISerializer<T>, ISerializerDelta<T>
        where T : unmanaged
    {
        static readonly Type _underlyingType;
        static readonly bool _isFlags;
        static readonly T[] _indexToValue;
        static readonly Dictionary<T, byte> _valueToIndex;

        public EnumSerializer() { }

        static EnumSerializer()
        {
            Assert.That(typeof(T).IsEnum, "Expected type {} to be an enum", typeof(T));
            _underlyingType = typeof(T).GetEnumUnderlyingType();
            _isFlags = typeof(T).IsDefined(typeof(FlagsAttribute), false);

            if (!_isFlags)
            {
                var values = (T[])Enum.GetValues(typeof(T));
                _indexToValue = values;
                _valueToIndex = new Dictionary<T, byte>(values.Length);

                for (int i = 0; i < values.Length; i++)
                {
                    Assert.That(
                        i <= byte.MaxValue,
                        "Enum {} has too many values ({}) for compact delta encoding",
                        typeof(T),
                        values.Length
                    );

                    if (!_valueToIndex.ContainsKey(values[i]))
                    {
                        _valueToIndex[values[i]] = (byte)i;
                    }
                }
            }
        }

        public void Serialize(in T value, ISerializationWriter writer)
        {
            if (_underlyingType == typeof(byte))
            {
                writer.Write<byte>("value", Convert.ToByte(value));
            }
            else if (_underlyingType == typeof(sbyte))
            {
                writer.Write<sbyte>("value", Convert.ToSByte(value));
            }
            else if (_underlyingType == typeof(short))
            {
                writer.Write<short>("value", Convert.ToInt16(value));
            }
            else if (_underlyingType == typeof(ushort))
            {
                writer.Write<ushort>("value", Convert.ToUInt16(value));
            }
            else if (_underlyingType == typeof(int))
            {
                writer.Write<int>("value", Convert.ToInt32(value));
            }
            else if (_underlyingType == typeof(uint))
            {
                writer.Write<uint>("value", Convert.ToUInt32(value));
            }
            else if (_underlyingType == typeof(long))
            {
                writer.Write<long>("value", Convert.ToInt64(value));
            }
            else if (_underlyingType == typeof(ulong))
            {
                writer.Write<ulong>("value", Convert.ToUInt64(value));
            }
            else
            {
                throw new NotImplementedException();
            }
        }

        public void Deserialize(ref T value, ISerializationReader reader)
        {
            if (_underlyingType == typeof(byte))
            {
                value = (T)(object)reader.Read<byte>("value");
            }
            else if (_underlyingType == typeof(sbyte))
            {
                value = (T)(object)reader.Read<sbyte>("value");
            }
            else if (_underlyingType == typeof(short))
            {
                value = (T)(object)reader.Read<short>("value");
            }
            else if (_underlyingType == typeof(ushort))
            {
                value = (T)(object)reader.Read<ushort>("value");
            }
            else if (_underlyingType == typeof(int))
            {
                value = (T)(object)reader.Read<int>("value");
            }
            else if (_underlyingType == typeof(uint))
            {
                value = (T)(object)reader.Read<uint>("value");
            }
            else if (_underlyingType == typeof(long))
            {
                value = (T)(object)reader.Read<long>("value");
            }
            else if (_underlyingType == typeof(ulong))
            {
                value = (T)(object)reader.Read<ulong>("value");
            }
            else
            {
                throw new NotImplementedException();
            }
        }

        public void SerializeDelta(in T value, in T baseValue, ISerializationWriter writer)
        {
            if (!_isFlags)
            {
                writer.WriteDelta<byte>("value", _valueToIndex[value], _valueToIndex[baseValue]);
                return;
            }

            SerializeDeltaUnderlying(value, baseValue, writer);
        }

        void SerializeDeltaUnderlying(in T value, in T baseValue, ISerializationWriter writer)
        {
            if (_underlyingType == typeof(byte))
            {
                writer.WriteDelta<byte>("value", Convert.ToByte(value), Convert.ToByte(baseValue));
            }
            else if (_underlyingType == typeof(sbyte))
            {
                writer.WriteDelta<sbyte>(
                    "value",
                    Convert.ToSByte(value),
                    Convert.ToSByte(baseValue)
                );
            }
            else if (_underlyingType == typeof(short))
            {
                writer.WriteDelta<short>(
                    "value",
                    Convert.ToInt16(value),
                    Convert.ToInt16(baseValue)
                );
            }
            else if (_underlyingType == typeof(ushort))
            {
                writer.WriteDelta<ushort>(
                    "value",
                    Convert.ToUInt16(value),
                    Convert.ToUInt16(baseValue)
                );
            }
            else if (_underlyingType == typeof(int))
            {
                writer.WriteDelta<int>("value", Convert.ToInt32(value), Convert.ToInt32(baseValue));
            }
            else if (_underlyingType == typeof(uint))
            {
                writer.WriteDelta<uint>(
                    "value",
                    Convert.ToUInt32(value),
                    Convert.ToUInt32(baseValue)
                );
            }
            else if (_underlyingType == typeof(long))
            {
                writer.WriteDelta<long>(
                    "value",
                    Convert.ToInt64(value),
                    Convert.ToInt64(baseValue)
                );
            }
            else if (_underlyingType == typeof(ulong))
            {
                writer.WriteDelta<ulong>(
                    "value",
                    Convert.ToUInt64(value),
                    Convert.ToUInt64(baseValue)
                );
            }
            else
            {
                throw new NotImplementedException();
            }
        }

        public void DeserializeDelta(ref T value, in T baseValue, ISerializationReader reader)
        {
            if (!_isFlags)
            {
                var index = reader.ReadDelta<byte>("value", _valueToIndex[baseValue]);
                value = _indexToValue[index];
                return;
            }

            DeserializeDeltaUnderlying(ref value, baseValue, reader);
        }

        void DeserializeDeltaUnderlying(ref T value, in T baseValue, ISerializationReader reader)
        {
            if (_underlyingType == typeof(byte))
            {
                value = (T)(object)reader.ReadDelta<byte>("value", Convert.ToByte(baseValue));
            }
            else if (_underlyingType == typeof(sbyte))
            {
                value = (T)(object)reader.ReadDelta<sbyte>("value", Convert.ToSByte(baseValue));
            }
            else if (_underlyingType == typeof(short))
            {
                value = (T)(object)reader.ReadDelta<short>("value", Convert.ToInt16(baseValue));
            }
            else if (_underlyingType == typeof(ushort))
            {
                value = (T)(object)reader.ReadDelta<ushort>("value", Convert.ToUInt16(baseValue));
            }
            else if (_underlyingType == typeof(int))
            {
                value = (T)(object)reader.ReadDelta<int>("value", Convert.ToInt32(baseValue));
            }
            else if (_underlyingType == typeof(uint))
            {
                value = (T)(object)reader.ReadDelta<uint>("value", Convert.ToUInt32(baseValue));
            }
            else if (_underlyingType == typeof(long))
            {
                value = (T)(object)reader.ReadDelta<long>("value", Convert.ToInt64(baseValue));
            }
            else if (_underlyingType == typeof(ulong))
            {
                value = (T)(object)reader.ReadDelta<ulong>("value", Convert.ToUInt64(baseValue));
            }
            else
            {
                throw new NotImplementedException();
            }
        }
    }
}
