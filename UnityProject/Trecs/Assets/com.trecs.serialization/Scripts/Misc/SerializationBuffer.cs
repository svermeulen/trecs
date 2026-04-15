using System;
using System.IO;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs.Serialization
{
    /// <summary>
    /// For cases where you are serializing / deserializing multiple times
    /// you can use this class to re-use the same buffers etc. and avoid allocs
    /// </summary>
    public class SerializationBuffer : IDisposable, ISerializationReader, ISerializationWriter
    {
        static readonly TrecsLog _log = new(nameof(SerializationBuffer));
        readonly MemoryStream _memoryStream;
        readonly BinaryReader _binaryReader;
        readonly BinarySerializationReader _reader;
        readonly BinaryWriter _binaryWriter;
        readonly BinarySerializationWriter _writer;

        bool _hasDisposed;
        State _state = State.Idle;

        public SerializationBuffer(SerializerRegistry serializerManager)
        {
            _memoryStream = new MemoryStream(1024);
            _binaryReader = new BinaryReader(_memoryStream);
            _reader = new BinarySerializationReader(serializerManager);
            _binaryWriter = new BinaryWriter(_memoryStream);
            _writer = new BinarySerializationWriter(serializerManager);
        }

        public MemoryStream MemoryStream
        {
            get
            {
                Assert.That(_state == State.Idle);
                return _memoryStream;
            }
        }

        // You shouldn't need to use this directly
        public BinaryReader BinaryReader
        {
            get
            {
                // Only allow using during idle, since otherwise you
                // should read via the reader
                Assert.That(_state == State.Idle);
                return _binaryReader;
            }
        }

        // You shouldn't need to use this directly
        public BinarySerializationWriter Writer
        {
            get
            {
                Assert.That(_state == State.Writing);
                return _writer;
            }
        }

        // You shouldn't need to use this directly
        public BinarySerializationReader Reader
        {
            get
            {
                Assert.That(_state == State.Reading);
                return _reader;
            }
        }

        // You shouldn't need to use this directly
        // NOTE: This is _writer.BinaryWriter, not _binaryWriter
        public BinaryWriter BinaryWriter
        {
            get
            {
                Assert.That(_state == State.Idle);
                return _binaryWriter;
            }
        }

        public long MemoryPosition
        {
            get
            {
                Assert.That(_state == State.Idle);
                return _memoryStream.Position;
            }
        }

        public long MemoryLength
        {
            get
            {
                Assert.That(_state == State.Idle);
                return _memoryStream.Length;
            }
        }

        /// <summary>
        /// Note that you can't call this multiple times for different parts of same data
        /// </summary>
        public long WriteAllDelta<T>(
            in T value,
            in T baseValue,
            int version,
            bool includeTypeChecks,
            ReadOnlyDenseHashSet<int> flags = default,
            bool enableMemoryTracking = false,
            bool allowUnclearedMemory = false
        )
        {
            Assert.That(_state == State.Idle);

            Assert.That(allowUnclearedMemory || MemoryIsCleared);

            _writer.Start(
                version: version,
                includeTypeChecks: includeTypeChecks,
                flags: flags,
                enableMemoryTracking: enableMemoryTracking
            );
            _writer.WriteDelta("value", value, baseValue);
            _writer.Complete(_binaryWriter);

            return _memoryStream.Position;
        }

        public object ReadAllObjectDelta(
            object baseValue,
            ReadOnlyDenseHashSet<int> flags = default
        )
        {
            Assert.That(_state == State.Idle);

            Assert.That(MemoryPosition == 0);

            _reader.Start(_binaryReader, flags: flags);
            var result = _reader.ReadObjectDelta("value", baseValue);
            _reader.Stop(verifySentinel: true);

            return result;
        }

        public T Read<T>(string path)
        {
            Assert.That(_state == State.Reading);
            return _reader.Read<T>(path);
        }

        public void Write<T>(string path, in T value)
        {
            Assert.That(_state == State.Writing);
            _writer.Write(path, value);
        }

        public void StartWrite(
            int version,
            bool includeTypeChecks,
            ReadOnlyDenseHashSet<int> flags = default,
            bool enableMemoryTracking = false
        )
        {
            Assert.That(_state == State.Idle);

            Assert.That(MemoryPosition == 0);

            _state = State.Writing;
            _writer.Start(
                version: version,
                includeTypeChecks: includeTypeChecks,
                flags,
                enableMemoryTracking: enableMemoryTracking
            );
        }

        public bool MemoryIsCleared
        {
            get { return _memoryStream.Position == 0 && _memoryStream.Length == 0; }
        }

        public void ResetMemoryPosition()
        {
            Assert.That(_state == State.Idle);
            _memoryStream.Position = 0;
        }

        public void ClearMemoryStream()
        {
            Assert.That(_state == State.Idle);
            _memoryStream.Position = 0;
            _memoryStream.SetLength(0);
        }

        public void StartRead(ReadOnlyDenseHashSet<int> flags = default)
        {
            Assert.That(_state == State.Idle);

            Assert.That(MemoryPosition == 0);

            _state = State.Reading;
            _reader.Start(_binaryReader, flags);
        }

        public void StopRead(bool verifySentinel)
        {
            Assert.That(_state == State.Reading);

            _state = State.Idle;
            _reader.Stop(verifySentinel: verifySentinel);
        }

        public long EndWrite()
        {
            Assert.That(_state == State.Writing);
            _state = State.Idle;

            _writer.Complete(_binaryWriter);

            var totalBytes = _memoryStream.Position;
            return totalBytes;
        }

        public void SaveMemoryToFile(string path)
        {
            Assert.That(_state == State.Idle);

            Assert.That(MemoryPosition == 0);

            // Then write the memory stream to file
            using var fileStream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.SequentialScan
            );
            _memoryStream.CopyTo(fileStream);
        }

        /// <summary>
        /// Note that you can't call this multiple times for different parts of same data
        /// </summary>
        public long WriteAll<T>(
            in T value,
            int version,
            bool includeTypeChecks,
            ReadOnlyDenseHashSet<int> flags = default,
            bool enableMemoryTracking = false,
            bool allowUnclearedMemory = false
        )
        {
            Assert.That(_state == State.Idle);

            Assert.That(allowUnclearedMemory || MemoryIsCleared);

            _writer.Start(
                version: version,
                includeTypeChecks: includeTypeChecks,
                flags: flags,
                enableMemoryTracking: enableMemoryTracking
            );
            _writer.Write("value", value);
            _writer.Complete(_binaryWriter);

            return _memoryStream.Position;
        }

        public object ReadAllObject(ReadOnlyDenseHashSet<int> flags = default)
        {
            Assert.That(_state == State.Idle);

            Assert.That(MemoryPosition == 0);

            _reader.Start(_binaryReader, flags: flags);
            var result = _reader.ReadObject("value");
            _reader.Stop(verifySentinel: true);
            return result;
        }

        // Use this if file can support multiple derived types
        public long WriteAllObject(
            object value,
            int version,
            bool includeTypeChecks,
            ReadOnlyDenseHashSet<int> flags = default,
            bool enableMemoryTracking = false
        )
        {
            Assert.That(_state == State.Idle);

            Assert.That(MemoryIsCleared);

            _writer.Start(
                version: version,
                includeTypeChecks: includeTypeChecks,
                flags,
                enableMemoryTracking: enableMemoryTracking
            );
            _writer.WriteObject("value", value);
            _writer.Complete(_binaryWriter);

            return _memoryStream.Position;
        }

        // NOTE: this hash is not suitable as a guid
        // For that, use GetMemoryStreamCollisionResistantGuid instead
        public int GetMemoryStreamHash()
        {
            Assert.That(!_hasDisposed);
            Assert.That(MemoryPosition == 0);
            Assert.That(_state == State.Idle);

            int length = (int)_memoryStream.Length;
            Assert.That(length > 0);

            byte[] buffer = _memoryStream.GetBuffer();

            uint unsignedHash = ByteHashCalculator.Run(buffer, length);
            return unchecked((int)unsignedHash);
        }

        /// <summary>
        /// Note that you can't call this multiple times for different parts of same data
        /// </summary>
        public T ReadAll<T>(
            ReadOnlyDenseHashSet<int> flags = default,
            bool allowNonZeroMemoryPosition = false
        )
        {
            Assert.That(_state == State.Idle);

            Assert.That(allowNonZeroMemoryPosition || MemoryPosition == 0);

            _reader.Start(_binaryReader, flags: flags);
            var result = _reader.Read<T>("value");
            _reader.Stop(verifySentinel: true);

            return result;
        }

        public void ReadAll<T>(
            ref T value,
            ReadOnlyDenseHashSet<int> flags = default,
            bool allowNonZeroMemoryPosition = false
        )
        {
            Assert.That(_state == State.Idle);

            Assert.That(allowNonZeroMemoryPosition || MemoryPosition == 0);

            _reader.Start(_binaryReader, flags: flags);
            _reader.Read<T>("value", ref value);
            _reader.Stop(verifySentinel: true);
        }

        // Use this if file can support multiple derived types
        public long WriteAllObjectDelta(
            object value,
            object baseValue,
            int version,
            bool includeTypeChecks,
            ReadOnlyDenseHashSet<int> flags = default,
            bool enableMemoryTracking = false
        )
        {
            Assert.That(_state == State.Idle);

            Assert.That(MemoryIsCleared);

            _writer.Start(
                version: version,
                includeTypeChecks: includeTypeChecks,
                flags,
                enableMemoryTracking: enableMemoryTracking
            );
            _writer.WriteObjectDelta("value", value, baseValue);
            _writer.Complete(_binaryWriter);

            return _memoryStream.Position;
        }

        public void LoadMemoryStreamFromBytes(byte[] bytes)
        {
            LoadMemoryStreamFromBytes(bytes, bytes.Length);
        }

        public void LoadMemoryStreamFromBytes(byte[] bytes, int length)
        {
            Assert.That(_state == State.Idle);

            Assert.That(MemoryIsCleared);

            _binaryWriter.Write(bytes, 0, length);
        }

        public void LoadMemoryStreamFromArraySegment(ArraySegment<byte> segment, int length)
        {
            Assert.That(_state == State.Idle);

            Assert.That(MemoryIsCleared);

            _binaryWriter.Write(segment.Array, segment.Offset, length);
        }

        public void LoadMemoryFromFile(string path)
        {
            Assert.That(_state == State.Idle);
            Assert.That(MemoryIsCleared);

            using var fileStream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan
            );
            fileStream.CopyTo(_memoryStream);
        }

        /// <summary>
        /// Note that you can't call this multiple times for different parts of same data
        /// </summary>
        public T ReadAllDelta<T>(
            in T baseValue,
            ReadOnlyDenseHashSet<int> flags = default,
            bool allowNonZeroMemoryPosition = false
        )
        {
            Assert.That(_state == State.Idle);

            Assert.That(allowNonZeroMemoryPosition || MemoryPosition == 0);

            _reader.Start(_binaryReader, flags);
            var result = _reader.ReadDelta<T>("value", baseValue);
            _reader.Stop(verifySentinel: true);
            return result;
        }

        public void Dispose()
        {
            Assert.That(_state == State.Idle, "Expected state to be Idle but was {}", _state);
            Assert.That(!_hasDisposed);
            _hasDisposed = true;

            _memoryStream.Dispose();
            _binaryWriter.Dispose();
            _binaryReader.Dispose();
            _writer.Dispose();
        }

        public void BlitRead<T>(string name, ref T value)
            where T : unmanaged
        {
            Assert.That(_state == State.Reading);
            _reader.BlitRead<T>(name, ref value);
        }

        public void BlitReadArray<T>(string name, T[] buffer, int count)
            where T : unmanaged
        {
            Assert.That(_state == State.Reading);
            _reader.BlitReadArray<T>(name, buffer, count);
        }

        public unsafe void BlitReadArrayPtr<T>(string name, T* value, int length)
            where T : unmanaged
        {
            Assert.That(_state == State.Reading);
            _reader.BlitReadArrayPtr<T>(name, value, length);
        }

        public bool ReadBit()
        {
            Assert.That(_state == State.Reading);
            return _reader.ReadBit();
        }

        public void Read<T>(string name, ref T value)
        {
            Assert.That(_state == State.Reading);
            _reader.Read(name, ref value);
        }

        public void ReadObject(string name, ref object value)
        {
            Assert.That(_state == State.Reading);
            _reader.ReadObject(name, ref value);
        }

        public string ReadString(string name)
        {
            Assert.That(_state == State.Reading);
            return _reader.ReadString(name);
        }

        public Type ReadTypeId(string name)
        {
            Assert.That(_state == State.Reading);
            return _reader.ReadTypeId(name);
        }

        public void BlitReadDelta<T>(string name, ref T value, in T baseValue)
            where T : unmanaged
        {
            Assert.That(_state == State.Reading);
            _reader.BlitReadDelta<T>(name, ref value, baseValue);
        }

        public void ReadDelta<T>(string name, ref T value, in T baseValue)
        {
            Assert.That(_state == State.Reading);
            _reader.ReadDelta(name, ref value, baseValue);
        }

        public void ReadObjectDelta(string name, ref object value, object baseValue)
        {
            Assert.That(_state == State.Reading);
            _reader.ReadObjectDelta(name, ref value, baseValue);
        }

        public string ReadStringDelta(string name, string baseValue)
        {
            Assert.That(_state == State.Reading);
            return _reader.ReadStringDelta(name, baseValue);
        }

        public void WriteBit(bool value)
        {
            Assert.That(_state == State.Writing);
            _writer.WriteBit(value);
        }

        public void WriteObject(string name, object value)
        {
            Assert.That(_state == State.Writing);
            _writer.WriteObject(name, value);
        }

        public void WriteString(string name, string value)
        {
            Assert.That(_state == State.Writing);
            _writer.WriteString(name, value);
        }

        public void BlitWrite<T>(string name, in T value)
            where T : unmanaged
        {
            Assert.That(_state == State.Writing);
            _writer.BlitWrite(name, value);
        }

        public unsafe void BlitWriteArrayPtr<T>(string name, T* value, int length)
            where T : unmanaged
        {
            Assert.That(_state == State.Writing);
            _writer.BlitWriteArrayPtr(name, value, length);
        }

        public void BlitWriteArray<T>(string name, T[] value, int count)
            where T : unmanaged
        {
            Assert.That(_state == State.Writing);
            _writer.BlitWriteArray(name, value, count);
        }

        public void WriteTypeId(string name, Type type)
        {
            Assert.That(_state == State.Writing);
            _writer.WriteTypeId(name, type);
        }

        public void WriteDelta<T>(string name, in T value, in T baseValue)
        {
            Assert.That(_state == State.Writing);
            _writer.WriteDelta(name, value, baseValue);
        }

        public void WriteObjectDelta(string name, object value, object baseValue)
        {
            Assert.That(_state == State.Writing);
            _writer.WriteObjectDelta(name, value, baseValue);
        }

        public void WriteStringDelta(string name, string value, string baseValue)
        {
            Assert.That(_state == State.Writing);
            _writer.WriteStringDelta(name, value, baseValue);
        }

        public string GetMemoryReport()
        {
            Assert.That(_state == State.Idle);
            return _writer.GetMemoryReport();
        }

        public void BlitWriteDelta<T>(string name, in T value, in T baseValue)
            where T : unmanaged
        {
            Assert.That(_state == State.Writing);
            _writer.BlitWriteDelta(name, value, baseValue);
        }

        public bool HasFlag(int flag)
        {
            if (_state == State.Reading)
            {
                return _reader.HasFlag(flag);
            }
            else if (_state == State.Writing)
            {
                return _writer.HasFlag(flag);
            }

            throw new InvalidOperationException("Cannot access Flags when not reading or writing.");
        }

        ReadOnlyDenseHashSet<int> ISerializationReader.Flags
        {
            get
            {
                Assert.That(_state == State.Reading);
                return _reader.Flags;
            }
        }

        ReadOnlyDenseHashSet<int> ISerializationWriter.Flags
        {
            get
            {
                Assert.That(_state == State.Writing);
                return _writer.Flags;
            }
        }

        int ISerializationReader.Version
        {
            get
            {
                Assert.That(_state == State.Reading);
                return _reader.Version;
            }
        }

        int ISerializationWriter.Version
        {
            get
            {
                Assert.That(_state == State.Writing);
                return _writer.Version;
            }
        }

        public long NumBytesWritten
        {
            get
            {
                Assert.That(_state == State.Writing);
                return _writer.NumBytesWritten;
            }
        }

        public void WriteBinary(string name, in SerializableByteArray value)
        {
            Assert.That(_state == State.Writing);
            _writer.WriteBinary(name, value);
        }

        public void ReadBinary(string name, ref SerializableByteArray value)
        {
            Assert.That(_state == State.Reading);
            _reader.ReadBinary(name, ref value);
        }

        public unsafe void BlitWriteRawBytes(string name, void* ptr, int numBytes)
        {
            Assert.That(_state == State.Writing);
            _writer.BlitWriteRawBytes(name, ptr, numBytes);
        }

        public unsafe void BlitReadRawBytes(string name, void* ptr, int numBytes)
        {
            Assert.That(_state == State.Reading);
            _reader.BlitReadRawBytes(name, ptr, numBytes);
        }

        public enum State
        {
            Idle,
            Reading,
            Writing,
        }
    }
}
