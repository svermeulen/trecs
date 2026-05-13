using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs.Serialization.Internal
{
    /// <summary>
    /// Serializer for enum types. Writes the value as its underlying primitive
    /// (byte/short/int/etc.). Delta encoding (for non-<c>[Flags]</c> enums) uses
    /// a compact byte index into the declared value set, so enums must declare
    /// at most 256 distinct values and must not declare aliases (duplicate
    /// values). Register via <see cref="SerializerRegistry.RegisterEnum{T}(bool)"/>.
    /// </summary>
    public sealed class EnumSerializer<T> : ISerializer<T>, ISerializerDelta<T>
        where T : unmanaged
    {
        enum UnderlyingKind : byte
        {
            Byte,
            SByte,
            Short,
            UShort,
            Int,
            UInt,
            Long,
            ULong,
        }

        const int MaxDeltaValues = byte.MaxValue + 1;

        static readonly UnderlyingKind _underlyingKind;
        static readonly bool _isFlags;
        static readonly T[] _indexToValue;
        static readonly Dictionary<T, byte> _valueToIndex;

        public EnumSerializer() { }

        static EnumSerializer()
        {
            Assert.That(typeof(T).IsEnum, "Expected type {} to be an enum", typeof(T));
            _underlyingKind = ResolveUnderlyingKind(typeof(T).GetEnumUnderlyingType());
            _isFlags = typeof(T).IsDefined(typeof(FlagsAttribute), false);

            if (!_isFlags)
            {
                var values = (T[])Enum.GetValues(typeof(T));
                Assert.That(
                    values.Length <= MaxDeltaValues,
                    "Enum {} declares {} values; delta encoding supports at most {}",
                    typeof(T),
                    values.Length,
                    MaxDeltaValues
                );

                _indexToValue = values;
                _valueToIndex = new Dictionary<T, byte>(values.Length);

                for (int i = 0; i < values.Length; i++)
                {
                    Assert.That(
                        !_valueToIndex.ContainsKey(values[i]),
                        "Enum {} has aliased values (multiple names mapping to the same numeric value); delta encoding cannot round-trip these — declare distinct values or remove EnumSerializer's delta registration",
                        typeof(T)
                    );
                    _valueToIndex[values[i]] = (byte)i;
                }
            }
        }

        public void Serialize(in T value, ISerializationWriter writer)
        {
            switch (_underlyingKind)
            {
                case UnderlyingKind.Byte:
                    writer.Write<byte>("value", Unsafe.As<T, byte>(ref Unsafe.AsRef(in value)));
                    return;
                case UnderlyingKind.SByte:
                    writer.Write<sbyte>("value", Unsafe.As<T, sbyte>(ref Unsafe.AsRef(in value)));
                    return;
                case UnderlyingKind.Short:
                    writer.Write<short>("value", Unsafe.As<T, short>(ref Unsafe.AsRef(in value)));
                    return;
                case UnderlyingKind.UShort:
                    writer.Write<ushort>("value", Unsafe.As<T, ushort>(ref Unsafe.AsRef(in value)));
                    return;
                case UnderlyingKind.Int:
                    writer.Write<int>("value", Unsafe.As<T, int>(ref Unsafe.AsRef(in value)));
                    return;
                case UnderlyingKind.UInt:
                    writer.Write<uint>("value", Unsafe.As<T, uint>(ref Unsafe.AsRef(in value)));
                    return;
                case UnderlyingKind.Long:
                    writer.Write<long>("value", Unsafe.As<T, long>(ref Unsafe.AsRef(in value)));
                    return;
                case UnderlyingKind.ULong:
                    writer.Write<ulong>("value", Unsafe.As<T, ulong>(ref Unsafe.AsRef(in value)));
                    return;
            }
            throw new NotImplementedException();
        }

        public void Deserialize(ref T value, ISerializationReader reader)
        {
            switch (_underlyingKind)
            {
                case UnderlyingKind.Byte:
                    Unsafe.As<T, byte>(ref value) = reader.Read<byte>("value");
                    return;
                case UnderlyingKind.SByte:
                    Unsafe.As<T, sbyte>(ref value) = reader.Read<sbyte>("value");
                    return;
                case UnderlyingKind.Short:
                    Unsafe.As<T, short>(ref value) = reader.Read<short>("value");
                    return;
                case UnderlyingKind.UShort:
                    Unsafe.As<T, ushort>(ref value) = reader.Read<ushort>("value");
                    return;
                case UnderlyingKind.Int:
                    Unsafe.As<T, int>(ref value) = reader.Read<int>("value");
                    return;
                case UnderlyingKind.UInt:
                    Unsafe.As<T, uint>(ref value) = reader.Read<uint>("value");
                    return;
                case UnderlyingKind.Long:
                    Unsafe.As<T, long>(ref value) = reader.Read<long>("value");
                    return;
                case UnderlyingKind.ULong:
                    Unsafe.As<T, ulong>(ref value) = reader.Read<ulong>("value");
                    return;
            }
            throw new NotImplementedException();
        }

        public void SerializeDelta(in T value, in T baseValue, ISerializationWriter writer)
        {
            if (!_isFlags)
            {
                if (!_valueToIndex.TryGetValue(value, out var valueIndex))
                {
                    throw new SerializationException(
                        $"EnumSerializer<{typeof(T)}> cannot delta-encode value '{value}' "
                            + "— not a declared constant of the enum"
                    );
                }
                if (!_valueToIndex.TryGetValue(baseValue, out var baseIndex))
                {
                    throw new SerializationException(
                        $"EnumSerializer<{typeof(T)}> cannot delta-encode baseValue '{baseValue}' "
                            + "— not a declared constant of the enum"
                    );
                }
                writer.WriteDelta<byte>("value", valueIndex, baseIndex);
                return;
            }

            SerializeDeltaUnderlying(value, baseValue, writer);
        }

        void SerializeDeltaUnderlying(in T value, in T baseValue, ISerializationWriter writer)
        {
            switch (_underlyingKind)
            {
                case UnderlyingKind.Byte:
                    writer.WriteDelta<byte>(
                        "value",
                        Unsafe.As<T, byte>(ref Unsafe.AsRef(in value)),
                        Unsafe.As<T, byte>(ref Unsafe.AsRef(in baseValue))
                    );
                    return;
                case UnderlyingKind.SByte:
                    writer.WriteDelta<sbyte>(
                        "value",
                        Unsafe.As<T, sbyte>(ref Unsafe.AsRef(in value)),
                        Unsafe.As<T, sbyte>(ref Unsafe.AsRef(in baseValue))
                    );
                    return;
                case UnderlyingKind.Short:
                    writer.WriteDelta<short>(
                        "value",
                        Unsafe.As<T, short>(ref Unsafe.AsRef(in value)),
                        Unsafe.As<T, short>(ref Unsafe.AsRef(in baseValue))
                    );
                    return;
                case UnderlyingKind.UShort:
                    writer.WriteDelta<ushort>(
                        "value",
                        Unsafe.As<T, ushort>(ref Unsafe.AsRef(in value)),
                        Unsafe.As<T, ushort>(ref Unsafe.AsRef(in baseValue))
                    );
                    return;
                case UnderlyingKind.Int:
                    writer.WriteDelta<int>(
                        "value",
                        Unsafe.As<T, int>(ref Unsafe.AsRef(in value)),
                        Unsafe.As<T, int>(ref Unsafe.AsRef(in baseValue))
                    );
                    return;
                case UnderlyingKind.UInt:
                    writer.WriteDelta<uint>(
                        "value",
                        Unsafe.As<T, uint>(ref Unsafe.AsRef(in value)),
                        Unsafe.As<T, uint>(ref Unsafe.AsRef(in baseValue))
                    );
                    return;
                case UnderlyingKind.Long:
                    writer.WriteDelta<long>(
                        "value",
                        Unsafe.As<T, long>(ref Unsafe.AsRef(in value)),
                        Unsafe.As<T, long>(ref Unsafe.AsRef(in baseValue))
                    );
                    return;
                case UnderlyingKind.ULong:
                    writer.WriteDelta<ulong>(
                        "value",
                        Unsafe.As<T, ulong>(ref Unsafe.AsRef(in value)),
                        Unsafe.As<T, ulong>(ref Unsafe.AsRef(in baseValue))
                    );
                    return;
            }
            throw new NotImplementedException();
        }

        public void DeserializeDelta(ref T value, in T baseValue, ISerializationReader reader)
        {
            if (!_isFlags)
            {
                if (!_valueToIndex.TryGetValue(baseValue, out var baseIndex))
                {
                    throw new SerializationException(
                        $"EnumSerializer<{typeof(T)}> cannot delta-decode against baseValue "
                            + $"'{baseValue}' — not a declared constant of the enum"
                    );
                }
                var index = reader.ReadDelta<byte>("value", baseIndex);
                if (index >= _indexToValue.Length)
                {
                    throw new SerializationException(
                        $"EnumSerializer<{typeof(T)}> read delta-index {index} but only "
                            + $"{_indexToValue.Length} values are declared — payload likely corrupted"
                    );
                }
                value = _indexToValue[index];
                return;
            }

            DeserializeDeltaUnderlying(ref value, baseValue, reader);
        }

        void DeserializeDeltaUnderlying(ref T value, in T baseValue, ISerializationReader reader)
        {
            switch (_underlyingKind)
            {
                case UnderlyingKind.Byte:
                    Unsafe.As<T, byte>(ref value) = reader.ReadDelta<byte>(
                        "value",
                        Unsafe.As<T, byte>(ref Unsafe.AsRef(in baseValue))
                    );
                    return;
                case UnderlyingKind.SByte:
                    Unsafe.As<T, sbyte>(ref value) = reader.ReadDelta<sbyte>(
                        "value",
                        Unsafe.As<T, sbyte>(ref Unsafe.AsRef(in baseValue))
                    );
                    return;
                case UnderlyingKind.Short:
                    Unsafe.As<T, short>(ref value) = reader.ReadDelta<short>(
                        "value",
                        Unsafe.As<T, short>(ref Unsafe.AsRef(in baseValue))
                    );
                    return;
                case UnderlyingKind.UShort:
                    Unsafe.As<T, ushort>(ref value) = reader.ReadDelta<ushort>(
                        "value",
                        Unsafe.As<T, ushort>(ref Unsafe.AsRef(in baseValue))
                    );
                    return;
                case UnderlyingKind.Int:
                    Unsafe.As<T, int>(ref value) = reader.ReadDelta<int>(
                        "value",
                        Unsafe.As<T, int>(ref Unsafe.AsRef(in baseValue))
                    );
                    return;
                case UnderlyingKind.UInt:
                    Unsafe.As<T, uint>(ref value) = reader.ReadDelta<uint>(
                        "value",
                        Unsafe.As<T, uint>(ref Unsafe.AsRef(in baseValue))
                    );
                    return;
                case UnderlyingKind.Long:
                    Unsafe.As<T, long>(ref value) = reader.ReadDelta<long>(
                        "value",
                        Unsafe.As<T, long>(ref Unsafe.AsRef(in baseValue))
                    );
                    return;
                case UnderlyingKind.ULong:
                    Unsafe.As<T, ulong>(ref value) = reader.ReadDelta<ulong>(
                        "value",
                        Unsafe.As<T, ulong>(ref Unsafe.AsRef(in baseValue))
                    );
                    return;
            }
            throw new NotImplementedException();
        }

        static UnderlyingKind ResolveUnderlyingKind(Type underlying)
        {
            if (underlying == typeof(byte))
                return UnderlyingKind.Byte;
            if (underlying == typeof(sbyte))
                return UnderlyingKind.SByte;
            if (underlying == typeof(short))
                return UnderlyingKind.Short;
            if (underlying == typeof(ushort))
                return UnderlyingKind.UShort;
            if (underlying == typeof(int))
                return UnderlyingKind.Int;
            if (underlying == typeof(uint))
                return UnderlyingKind.UInt;
            if (underlying == typeof(long))
                return UnderlyingKind.Long;
            if (underlying == typeof(ulong))
                return UnderlyingKind.ULong;
            throw new NotImplementedException(
                $"Unsupported enum underlying type '{underlying}' for {typeof(T)}"
            );
        }
    }
}
