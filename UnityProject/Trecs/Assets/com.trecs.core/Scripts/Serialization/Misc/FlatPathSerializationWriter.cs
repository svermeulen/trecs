#if DEBUG

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Trecs.Internal;
using Trecs.Serialization;

namespace Trecs
{
    /// <summary>
    /// DEBUG-only <see cref="ISerializationWriter"/> that emits each scalar at
    /// its fully-qualified dotted path on its own line, one line per leaf
    /// value. Intended for desync debugging: capture two snapshots, run them
    /// through <c>diff</c>, and the differing lines tell you exactly which
    /// component values diverged. The whole class is compiled out of release
    /// builds.
    /// <para>
    /// Output looks like:
    /// <code>
    /// Version = 7
    /// Flags = 0
    /// World.RngSeed = 12345
    /// World.ComponentArrays.Group0.Group._tags = 3
    /// World.ComponentArrays.Group0.Component0.TypeId._typeIndex = 42
    /// World.ComponentArrays.Group0.Component0.Count = 5
    /// </code>
    /// </para>
    /// </summary>
    public sealed class FlatPathSerializationWriter : ISerializationWriter
    {
        const int HashBytes = 8;

        // BlitWrite reflects into struct fields when the type isn't a
        // recognised primitive. Cache so each type pays the reflection cost
        // once per debug snapshot session.
        static readonly Dictionary<Type, FieldInfo[]> _structFieldsCache = new();

        readonly SerializerRegistry _serializerManager;
        readonly TextWriter _output;

        readonly StringBuilder _path = new();
        readonly Stack<int> _pathCheckpoints = new();

        long _flags;
        int _version;
        int _bitIndex;
        bool _hasStarted;

        public FlatPathSerializationWriter(TextWriter output, SerializerRegistry serializerManager)
        {
            _output = output;
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

        public long NumBytesWritten => 0;

        public void Start(int version, long flags = 0)
        {
            TrecsDebugAssert.That(!_hasStarted);

            var reservedUnused =
                flags & SerializationFlags.ReservedMask & ~SerializationFlags.AllDefinedMask;
            TrecsDebugAssert.That(
                reservedUnused == 0,
                "Flag bits {0:X} are reserved for Trecs and must not be set by app code. Use bits >= SerializationFlags.FirstUserBitIndex ({1}) for app-defined flags.",
                reservedUnused,
                SerializationFlags.FirstUserBitIndex
            );

            _flags = flags;
            _version = version;
            _bitIndex = 0;
            _hasStarted = true;
            _path.Clear();
            _pathCheckpoints.Clear();

            EmitLeaf("Version", _version.ToString(CultureInfo.InvariantCulture));
            EmitLeaf("Flags", _flags.ToString(CultureInfo.InvariantCulture));
        }

        public void Complete()
        {
            TrecsDebugAssert.That(_hasStarted);
            TrecsDebugAssert.That(
                _pathCheckpoints.Count == 0,
                "Path stack not empty at Complete — {0} unmatched Push(es)",
                _pathCheckpoints.Count
            );
            _hasStarted = false;
        }

        public void WriteBit(bool value)
        {
            TrecsDebugAssert.That(_hasStarted);
            EmitLeaf(
                "_b" + _bitIndex.ToString(CultureInfo.InvariantCulture),
                value ? "true" : "false"
            );
            _bitIndex++;
        }

        public void WriteString(string name, string value)
        {
            TrecsDebugAssert.That(_hasStarted);
            EmitLeaf(name, value == null ? "null" : QuoteString(value));
        }

        public void WriteTypeId(string name, Type type)
        {
            TrecsDebugAssert.That(_hasStarted);
            EmitLeaf(name, type == null ? "null" : type.AssemblyQualifiedName);
        }

        public void WriteObject(string name, object value)
        {
            TrecsDebugAssert.That(_hasStarted);

            if (value == null)
            {
                EmitLeaf(name, "null");
                return;
            }

            var type = value.GetType();
            PushName(name);
            var savedBitIndex = _bitIndex;
            _bitIndex = 0;
            EmitLeaf("_type", type.AssemblyQualifiedName);

            var serializer = _serializerManager.GetSerializer(type);
            serializer.SerializeObject(value, this);

            _bitIndex = savedBitIndex;
            Pop();
        }

        public void Write<T>(string name, in T value)
        {
            TrecsDebugAssert.That(_hasStarted);

            var type = typeof(T);

            if (!type.IsValueType && value == null)
            {
                EmitLeaf(name, "null");
                return;
            }

            TrecsDebugAssert.That(
                type.IsValueType
                    || type == typeof(Type)
                    || type == typeof(string)
                    || value.GetType() == type,
                "When serializing from base class/interface, use WriteObject instead"
            );

            var serializer = _serializerManager.GetSerializer<T>();
            var serializerType = serializer.GetType();

            // Bypass the wrapper-frame for primitive serializers so the
            // output reads as "Foo = 42" instead of "Foo.Value = 42".
            if (
                serializerType.IsGenericType
                && serializerType.GetGenericTypeDefinition() == typeof(BlitSerializer<>)
            )
            {
                BlitWriteImpl(name, value, type);
            }
            else if (serializerType == typeof(BoolSerializer))
            {
                EmitLeaf(name, ((bool)(object)value) ? "true" : "false");
            }
            else
            {
                PushName(name);
                var savedBitIndex = _bitIndex;
                _bitIndex = 0;
                serializer.Serialize(value, this);
                _bitIndex = savedBitIndex;
                Pop();
            }
        }

        public void BlitWrite<T>(string name, in T value)
            where T : unmanaged
        {
            TrecsDebugAssert.That(_hasStarted);
            BlitWriteImpl(name, value, typeof(T));
        }

        public unsafe void BlitWriteArrayPtr<T>(string name, T* value, int length)
            where T : unmanaged
        {
            TrecsDebugAssert.That(_hasStarted);

            PushName(name);
            var elementType = typeof(T);
            for (int i = 0; i < length; i++)
            {
                PushIndex(i);
                BlitWriteValue(value[i], elementType);
                Pop();
            }
            Pop();
        }

        public unsafe void BlitWriteArray<T>(string name, T[] value, int count)
            where T : unmanaged
        {
            TrecsDebugAssert.That(_hasStarted);

            PushName(name);
            var elementType = typeof(T);
            for (int i = 0; i < count; i++)
            {
                PushIndex(i);
                BlitWriteValue(value[i], elementType);
                Pop();
            }
            Pop();
        }

        public unsafe void BlitWriteRawBytes(string name, void* ptr, int numBytes)
        {
            TrecsDebugAssert.That(_hasStarted);
            EmitLeaf(name, FormatBytesSummary(ptr, numBytes));
        }

        public void WriteBytes(string name, byte[] buffer, int offset, int count)
        {
            TrecsDebugAssert.That(_hasStarted);
            EmitLeaf(name, FormatBytesSummary(buffer, offset, count));
        }

        public void BlitWriteDelta<T>(string name, in T value, in T baseValue)
            where T : unmanaged
        {
            throw new NotImplementedException(
                "Desync writer does not support delta serialization — capture full-state snapshots instead."
            );
        }

        public void WriteObjectDelta(string name, object value, object baseValue)
        {
            throw new NotImplementedException(
                "Desync writer does not support delta serialization — capture full-state snapshots instead."
            );
        }

        public void WriteDelta<T>(string name, in T value, in T baseValue)
        {
            throw new NotImplementedException(
                "Desync writer does not support delta serialization — capture full-state snapshots instead."
            );
        }

        public void WriteStringDelta(string name, string value, string baseValue)
        {
            throw new NotImplementedException(
                "Desync writer does not support delta serialization — capture full-state snapshots instead."
            );
        }

        void BlitWriteImpl<T>(string name, in T value, Type type)
        {
            if (TryFormatPrimitive(value, type, out var formatted))
            {
                EmitLeaf(name, formatted);
                return;
            }

            PushName(name);
            ExpandStructFields(value, type);
            Pop();
        }

        void BlitWriteValue(object value, Type type)
        {
            if (TryFormatPrimitive(value, type, out var formatted))
            {
                EmitCurrentPath(formatted);
                return;
            }

            ExpandStructFields(value, type);
        }

        void ExpandStructFields(object value, Type type)
        {
            var fields = GetCachedFields(type);
            foreach (var field in fields)
            {
                PushName(field.Name);
                var fieldValue = field.GetValue(value);
                var fieldType = field.FieldType;
                if (TryFormatPrimitive(fieldValue, fieldType, out var formatted))
                {
                    EmitCurrentPath(formatted);
                }
                else
                {
                    ExpandStructFields(fieldValue, fieldType);
                }
                Pop();
            }
        }

        static FieldInfo[] GetCachedFields(Type type)
        {
            // _structFieldsCache is shared across all writer instances —
            // gate access to the main thread so concurrent debug captures
            // (e.g. one accidentally driven from a worker thread) can't
            // corrupt the dictionary. The cache is the only shared state
            // in this class; per-instance state (path stack, bit index,
            // output) lives on the writer itself.
            TrecsDebugAssert.That(UnityThreadHelper.IsMainThread);
            if (!_structFieldsCache.TryGetValue(type, out var fields))
            {
                fields = type.GetFields(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                );
                _structFieldsCache.Add(type, fields);
            }
            return fields;
        }

        static bool TryFormatPrimitive(object value, Type type, out string formatted)
        {
            if (type.IsEnum)
            {
                formatted = type.Name + "." + value;
                return true;
            }

            switch (Type.GetTypeCode(type))
            {
                case TypeCode.Boolean:
                    formatted = ((bool)value) ? "true" : "false";
                    return true;
                case TypeCode.Byte:
                case TypeCode.SByte:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Int64:
                case TypeCode.UInt64:
                    formatted = ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture);
                    return true;
                case TypeCode.Single:
                    formatted = ((float)value).ToString("G9", CultureInfo.InvariantCulture);
                    return true;
                case TypeCode.Double:
                    formatted = ((double)value).ToString("G17", CultureInfo.InvariantCulture);
                    return true;
                case TypeCode.Char:
                    formatted = "'" + EscapeChar((char)value) + "'";
                    return true;
                case TypeCode.String:
                    formatted = value == null ? "null" : QuoteString((string)value);
                    return true;
            }

            if (type == typeof(IntPtr) || type == typeof(UIntPtr))
            {
                formatted = value.ToString();
                return true;
            }

            formatted = null;
            return false;
        }

        void EmitLeaf(string name, string formattedValue)
        {
            PushName(name);
            EmitCurrentPath(formattedValue);
            Pop();
        }

        void EmitCurrentPath(string formattedValue)
        {
            _output.Write(_path.ToString());
            _output.Write(" = ");
            _output.Write(formattedValue);
            _output.Write('\n');
        }

        public void PushScope(string name) => PushName(name);

        public void PopScope() => Pop();

        void PushName(string name)
        {
            TrecsDebugAssert.IsNotNull(name);
            _pathCheckpoints.Push(_path.Length);
            if (_path.Length > 0)
            {
                _path.Append('.');
            }
            _path.Append(name);
        }

        void PushIndex(int index)
        {
            _pathCheckpoints.Push(_path.Length);
            _path.Append('[');
            _path.Append(index);
            _path.Append(']');
        }

        void Pop()
        {
            _path.Length = _pathCheckpoints.Pop();
        }

        unsafe string FormatBytesSummary(void* ptr, int numBytes)
        {
            if (numBytes == 0)
            {
                return "bytes(len=0)";
            }
            using var sha = SHA256.Create();
            var bytes = new byte[numBytes];
            fixed (byte* dest = bytes)
            {
                Buffer.MemoryCopy(ptr, dest, numBytes, numBytes);
            }
            var hash = sha.ComputeHash(bytes);
            return BuildBytesSummary(numBytes, hash);
        }

        string FormatBytesSummary(byte[] buffer, int offset, int count)
        {
            if (count == 0)
            {
                return "bytes(len=0)";
            }
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(buffer, offset, count);
            return BuildBytesSummary(count, hash);
        }

        static string BuildBytesSummary(int length, byte[] hash)
        {
            var sb = new StringBuilder(32);
            sb.Append("bytes(len=");
            sb.Append(length);
            sb.Append(", sha256=");
            for (int i = 0; i < HashBytes && i < hash.Length; i++)
            {
                sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
            }
            sb.Append(')');
            return sb.ToString();
        }

        static string QuoteString(string value)
        {
            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            foreach (var ch in value)
            {
                sb.Append(EscapeChar(ch));
            }
            sb.Append('"');
            return sb.ToString();
        }

        static string EscapeChar(char ch)
        {
            switch (ch)
            {
                case '\\':
                    return "\\\\";
                case '"':
                    return "\\\"";
                case '\n':
                    return "\\n";
                case '\r':
                    return "\\r";
                case '\t':
                    return "\\t";
                case '\0':
                    return "\\0";
                case '\b':
                    return "\\b";
                case '\f':
                    return "\\f";
                default:
                    return ch.ToString();
            }
        }
    }
}

#endif
