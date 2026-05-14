using System;
using System.IO;

namespace Trecs.Internal
{
    /// <summary>
    /// For cases where you are serializing / deserializing multiple times
    /// you can use this class to re-use the same buffers etc. and avoid allocs
    /// </summary>
    public sealed class SerializationBuffer
        : IDisposable,
            ISerializationReader,
            ISerializationWriter
    {
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

        /// <summary>
        /// Current read/write state of the buffer. Useful for defensive caller
        /// code that needs to know whether it's safe to reset or reuse the
        /// buffer without relying on exception-based signalling.
        /// </summary>
        public State CurrentState => _state;

        public MemoryStream MemoryStream
        {
            get
            {
                TrecsAssert.That(_state == State.Idle);
                return _memoryStream;
            }
        }

        // Only allow during idle — once a Start* is in progress, callers
        // must read/write through the buffer's own methods, not the
        // underlying BinaryReader.
        public BinaryReader BinaryReader
        {
            get
            {
                TrecsAssert.That(_state == State.Idle);
                return _binaryReader;
            }
        }

        public BinarySerializationWriter Writer
        {
            get
            {
                TrecsAssert.That(_state == State.Writing);
                return _writer;
            }
        }

        public BinarySerializationReader Reader
        {
            get
            {
                TrecsAssert.That(_state == State.Reading);
                return _reader;
            }
        }

        public BinaryWriter BinaryWriter
        {
            get
            {
                TrecsAssert.That(_state == State.Idle);
                return _binaryWriter;
            }
        }

        public long MemoryPosition
        {
            get
            {
                TrecsAssert.That(_state == State.Idle);
                return _memoryStream.Position;
            }
        }

        public long MemoryLength
        {
            get
            {
                TrecsAssert.That(_state == State.Idle);
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
            long flags = 0,
            bool enableMemoryTracking = false,
            bool allowUnclearedMemory = false
        )
        {
            TrecsAssert.That(_state == State.Idle);

            TrecsAssert.That(allowUnclearedMemory || MemoryIsCleared);

            try
            {
                _writer.Start(
                    version: version,
                    includeTypeChecks: includeTypeChecks,
                    flags: flags,
                    enableMemoryTracking: enableMemoryTracking
                );
                _writer.WriteDelta("Value", value, baseValue);
                _writer.Complete(_binaryWriter);
            }
            catch
            {
                ResetForErrorRecovery();
                throw;
            }

            return _memoryStream.Position;
        }

        public object ReadAllObjectDelta(object baseValue)
        {
            TrecsAssert.That(_state == State.Idle);

            TrecsAssert.That(MemoryPosition == 0);

            try
            {
                _reader.Start(_binaryReader);
                var result = _reader.ReadObjectDelta("Value", baseValue);
                _reader.Stop(verifySentinel: true);

                return result;
            }
            catch
            {
                ResetForErrorRecovery();
                throw;
            }
        }

        public T Read<T>(string path)
        {
            TrecsAssert.That(_state == State.Reading);
            return _reader.Read<T>(path);
        }

        public void Write<T>(string path, in T value)
        {
            TrecsAssert.That(_state == State.Writing);
            _writer.Write(path, value);
        }

        public void StartWrite(
            int version,
            bool includeTypeChecks,
            long flags = 0,
            bool enableMemoryTracking = false
        )
        {
            TrecsAssert.That(_state == State.Idle);

            TrecsAssert.That(MemoryPosition == 0);

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
            TrecsAssert.That(_state == State.Idle);
            _memoryStream.Position = 0;
        }

        public void ClearMemoryStream()
        {
            TrecsAssert.That(_state == State.Idle);
            _memoryStream.Position = 0;
            _memoryStream.SetLength(0);
        }

        /// <summary>
        /// Forcibly return the buffer to <see cref="State.Idle"/> regardless of
        /// the current state. Intended for exception recovery in handler
        /// internals — on a successful call, the normal <see cref="EndWrite"/>
        /// / <see cref="StopRead"/> path already leaves the buffer idle.
        /// Discards any partially-written/partially-read data.
        /// </summary>
        public void ResetForErrorRecovery()
        {
            _state = State.Idle;
            _memoryStream.Position = 0;
            _memoryStream.SetLength(0);
            _reader.ResetForErrorRecovery();
            _writer.ResetForErrorRecovery();
        }

        public void StartRead()
        {
            TrecsAssert.That(_state == State.Idle);

            TrecsAssert.That(MemoryPosition == 0);

            _state = State.Reading;
            _reader.Start(_binaryReader);
        }

        /// <summary>
        /// Read just the header at the current memory position without starting
        /// a full read. Does not advance the memory position. Intended for
        /// pre-flight validation (e.g. asserting required flags before calling
        /// <see cref="StartRead"/>).
        /// </summary>
        public PayloadHeader PeekHeader()
        {
            TrecsAssert.That(_state == State.Idle);
            return PayloadHeader.Peek(_memoryStream);
        }

        public void StopRead(bool verifySentinel)
        {
            TrecsAssert.That(_state == State.Reading);

            _state = State.Idle;
            _reader.Stop(verifySentinel: verifySentinel);
        }

        public long EndWrite()
        {
            TrecsAssert.That(_state == State.Writing);
            _state = State.Idle;

            _writer.Complete(_binaryWriter);

            var totalBytes = _memoryStream.Position;
            return totalBytes;
        }

        public void SaveMemoryToFile(string path)
        {
            TrecsAssert.That(_state == State.Idle);

            TrecsAssert.That(MemoryPosition == 0);

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
            long flags = 0,
            bool enableMemoryTracking = false,
            bool allowUnclearedMemory = false
        )
        {
            TrecsAssert.That(_state == State.Idle);

            TrecsAssert.That(allowUnclearedMemory || MemoryIsCleared);

            try
            {
                _writer.Start(
                    version: version,
                    includeTypeChecks: includeTypeChecks,
                    flags: flags,
                    enableMemoryTracking: enableMemoryTracking
                );
                _writer.Write("Value", value);
                _writer.Complete(_binaryWriter);
            }
            catch
            {
                ResetForErrorRecovery();
                throw;
            }

            return _memoryStream.Position;
        }

        public object ReadAllObject()
        {
            TrecsAssert.That(_state == State.Idle);

            TrecsAssert.That(MemoryPosition == 0);

            try
            {
                _reader.Start(_binaryReader);
                var result = _reader.ReadObject("Value");
                _reader.Stop(verifySentinel: true);
                return result;
            }
            catch
            {
                ResetForErrorRecovery();
                throw;
            }
        }

        // Use this if file can support multiple derived types
        public long WriteAllObject(
            object value,
            int version,
            bool includeTypeChecks,
            long flags = 0,
            bool enableMemoryTracking = false
        )
        {
            TrecsAssert.That(_state == State.Idle);

            TrecsAssert.That(MemoryIsCleared);

            try
            {
                _writer.Start(
                    version: version,
                    includeTypeChecks: includeTypeChecks,
                    flags,
                    enableMemoryTracking: enableMemoryTracking
                );
                _writer.WriteObject("Value", value);
                _writer.Complete(_binaryWriter);
            }
            catch
            {
                ResetForErrorRecovery();
                throw;
            }

            return _memoryStream.Position;
        }

        // NOTE: this hash is not suitable as a guid
        // For that, use GetMemoryStreamCollisionResistantGuid instead
        [Obsolete("Use ComputeChecksum() instead — same hash, returned as uint.")]
        public int GetMemoryStreamHash()
        {
            return unchecked((int)ComputeChecksum());
        }

        /// <summary>
        /// Compute a checksum over the currently-written bytes. Safely handles
        /// the <see cref="MemoryStream.GetBuffer"/> / <see cref="MemoryStream.Length"/>
        /// discipline so callers cannot accidentally hash uninitialized trailing
        /// bytes (which would produce a non-deterministic checksum and break
        /// replay / desync detection).
        /// </summary>
        public uint ComputeChecksum()
        {
            TrecsAssert.That(!_hasDisposed);
            TrecsAssert.That(_state == State.Idle);

            int length = (int)_memoryStream.Length;
            byte[] buffer = _memoryStream.GetBuffer();

            return ByteHashCalculator.Run(buffer, length);
        }

        /// <summary>
        /// Note that you can't call this multiple times for different parts of same data
        /// </summary>
        public T ReadAll<T>(bool allowNonZeroMemoryPosition = false)
        {
            TrecsAssert.That(_state == State.Idle);

            TrecsAssert.That(allowNonZeroMemoryPosition || MemoryPosition == 0);

            try
            {
                _reader.Start(_binaryReader);
                var result = _reader.Read<T>("Value");
                _reader.Stop(verifySentinel: true);

                return result;
            }
            catch
            {
                ResetForErrorRecovery();
                throw;
            }
        }

        public void ReadAll<T>(ref T value, bool allowNonZeroMemoryPosition = false)
        {
            TrecsAssert.That(_state == State.Idle);

            TrecsAssert.That(allowNonZeroMemoryPosition || MemoryPosition == 0);

            try
            {
                _reader.Start(_binaryReader);
                _reader.Read<T>("Value", ref value);
                _reader.Stop(verifySentinel: true);
            }
            catch
            {
                ResetForErrorRecovery();
                throw;
            }
        }

        // Use this if file can support multiple derived types
        public long WriteAllObjectDelta(
            object value,
            object baseValue,
            int version,
            bool includeTypeChecks,
            long flags = 0,
            bool enableMemoryTracking = false
        )
        {
            TrecsAssert.That(_state == State.Idle);

            TrecsAssert.That(MemoryIsCleared);

            try
            {
                _writer.Start(
                    version: version,
                    includeTypeChecks: includeTypeChecks,
                    flags,
                    enableMemoryTracking: enableMemoryTracking
                );
                _writer.WriteObjectDelta("Value", value, baseValue);
                _writer.Complete(_binaryWriter);
            }
            catch
            {
                ResetForErrorRecovery();
                throw;
            }

            return _memoryStream.Position;
        }

        public void LoadMemoryStreamFromBytes(byte[] bytes)
        {
            LoadMemoryStreamFromBytes(bytes, bytes.Length);
        }

        public void LoadMemoryStreamFromBytes(byte[] bytes, int length)
        {
            TrecsAssert.That(_state == State.Idle);

            TrecsAssert.That(MemoryIsCleared);

            _binaryWriter.Write(bytes, 0, length);
        }

        public void LoadMemoryStreamFromArraySegment(ArraySegment<byte> segment, int length)
        {
            TrecsAssert.That(_state == State.Idle);

            TrecsAssert.That(MemoryIsCleared);

            _binaryWriter.Write(segment.Array, segment.Offset, length);
        }

        public void LoadMemoryFromFile(string path)
        {
            TrecsAssert.That(_state == State.Idle);
            TrecsAssert.That(MemoryIsCleared);

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
        public T ReadAllDelta<T>(in T baseValue, bool allowNonZeroMemoryPosition = false)
        {
            TrecsAssert.That(_state == State.Idle);

            TrecsAssert.That(allowNonZeroMemoryPosition || MemoryPosition == 0);

            try
            {
                _reader.Start(_binaryReader);
                var result = _reader.ReadDelta<T>("Value", baseValue);
                _reader.Stop(verifySentinel: true);
                return result;
            }
            catch
            {
                ResetForErrorRecovery();
                throw;
            }
        }

        public void Dispose()
        {
            TrecsAssert.That(_state == State.Idle, "Expected state to be Idle but was {0}", _state);
            TrecsAssert.That(!_hasDisposed);
            _hasDisposed = true;

            _memoryStream.Dispose();
            _binaryWriter.Dispose();
            _binaryReader.Dispose();
            _writer.Dispose();
        }

        public void BlitRead<T>(string name, ref T value)
            where T : unmanaged
        {
            TrecsAssert.That(_state == State.Reading);
            _reader.BlitRead<T>(name, ref value);
        }

        public void BlitReadArray<T>(string name, T[] buffer, int count)
            where T : unmanaged
        {
            TrecsAssert.That(_state == State.Reading);
            _reader.BlitReadArray<T>(name, buffer, count);
        }

        public unsafe void BlitReadArrayPtr<T>(string name, T* value, int length)
            where T : unmanaged
        {
            TrecsAssert.That(_state == State.Reading);
            _reader.BlitReadArrayPtr<T>(name, value, length);
        }

        public bool ReadBit()
        {
            TrecsAssert.That(_state == State.Reading);
            return _reader.ReadBit();
        }

        public void Read<T>(string name, ref T value)
        {
            TrecsAssert.That(_state == State.Reading);
            _reader.Read(name, ref value);
        }

        public void ReadObject(string name, ref object value)
        {
            TrecsAssert.That(_state == State.Reading);
            _reader.ReadObject(name, ref value);
        }

        public string ReadString(string name)
        {
            TrecsAssert.That(_state == State.Reading);
            return _reader.ReadString(name);
        }

        public Type ReadTypeId(string name)
        {
            TrecsAssert.That(_state == State.Reading);
            return _reader.ReadTypeId(name);
        }

        public void BlitReadDelta<T>(string name, ref T value, in T baseValue)
            where T : unmanaged
        {
            TrecsAssert.That(_state == State.Reading);
            _reader.BlitReadDelta<T>(name, ref value, baseValue);
        }

        public void ReadDelta<T>(string name, ref T value, in T baseValue)
        {
            TrecsAssert.That(_state == State.Reading);
            _reader.ReadDelta(name, ref value, baseValue);
        }

        public void ReadObjectDelta(string name, ref object value, object baseValue)
        {
            TrecsAssert.That(_state == State.Reading);
            _reader.ReadObjectDelta(name, ref value, baseValue);
        }

        public string ReadStringDelta(string name, string baseValue)
        {
            TrecsAssert.That(_state == State.Reading);
            return _reader.ReadStringDelta(name, baseValue);
        }

        public void WriteBit(bool value)
        {
            TrecsAssert.That(_state == State.Writing);
            _writer.WriteBit(value);
        }

        public void WriteObject(string name, object value)
        {
            TrecsAssert.That(_state == State.Writing);
            _writer.WriteObject(name, value);
        }

        public void WriteString(string name, string value)
        {
            TrecsAssert.That(_state == State.Writing);
            _writer.WriteString(name, value);
        }

        public void BlitWrite<T>(string name, in T value)
            where T : unmanaged
        {
            TrecsAssert.That(_state == State.Writing);
            _writer.BlitWrite(name, value);
        }

        public unsafe void BlitWriteArrayPtr<T>(string name, T* value, int length)
            where T : unmanaged
        {
            TrecsAssert.That(_state == State.Writing);
            _writer.BlitWriteArrayPtr(name, value, length);
        }

        public void BlitWriteArray<T>(string name, T[] value, int count)
            where T : unmanaged
        {
            TrecsAssert.That(_state == State.Writing);
            _writer.BlitWriteArray(name, value, count);
        }

        public void WriteTypeId(string name, Type type)
        {
            TrecsAssert.That(_state == State.Writing);
            _writer.WriteTypeId(name, type);
        }

        public void WriteDelta<T>(string name, in T value, in T baseValue)
        {
            TrecsAssert.That(_state == State.Writing);
            _writer.WriteDelta(name, value, baseValue);
        }

        public void WriteObjectDelta(string name, object value, object baseValue)
        {
            TrecsAssert.That(_state == State.Writing);
            _writer.WriteObjectDelta(name, value, baseValue);
        }

        public void WriteStringDelta(string name, string value, string baseValue)
        {
            TrecsAssert.That(_state == State.Writing);
            _writer.WriteStringDelta(name, value, baseValue);
        }

        public string GetMemoryReport()
        {
            TrecsAssert.That(_state == State.Idle);
            return _writer.GetMemoryReport();
        }

        public void BlitWriteDelta<T>(string name, in T value, in T baseValue)
            where T : unmanaged
        {
            TrecsAssert.That(_state == State.Writing);
            _writer.BlitWriteDelta(name, value, baseValue);
        }

        public bool HasFlag(long flag)
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

        long ISerializationReader.Flags
        {
            get
            {
                TrecsAssert.That(_state == State.Reading);
                return _reader.Flags;
            }
        }

        long ISerializationWriter.Flags
        {
            get
            {
                TrecsAssert.That(_state == State.Writing);
                return _writer.Flags;
            }
        }

        int ISerializationReader.Version
        {
            get
            {
                TrecsAssert.That(_state == State.Reading);
                return _reader.Version;
            }
        }

        int ISerializationWriter.Version
        {
            get
            {
                TrecsAssert.That(_state == State.Writing);
                return _writer.Version;
            }
        }

        public long NumBytesWritten
        {
            get
            {
                TrecsAssert.That(_state == State.Writing);
                return _writer.NumBytesWritten;
            }
        }

        public void WriteBytes(string name, byte[] buffer, int offset, int count)
        {
            TrecsAssert.That(_state == State.Writing);
            _writer.WriteBytes(name, buffer, offset, count);
        }

        public int ReadBytes(string name, ref byte[] buffer)
        {
            TrecsAssert.That(_state == State.Reading);
            return _reader.ReadBytes(name, ref buffer);
        }

        public unsafe void BlitWriteRawBytes(string name, void* ptr, int numBytes)
        {
            TrecsAssert.That(_state == State.Writing);
            _writer.BlitWriteRawBytes(name, ptr, numBytes);
        }

        public unsafe void BlitReadRawBytes(string name, void* ptr, int numBytes)
        {
            TrecsAssert.That(_state == State.Reading);
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
