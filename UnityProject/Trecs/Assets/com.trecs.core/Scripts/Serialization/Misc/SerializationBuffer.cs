using System;
using System.Buffers.Binary;
using System.IO;

namespace Trecs.Internal
{
    /// <summary>
    /// Reusable buffer for repeated serialization/deserialization to avoid allocations.
    /// </summary>
    public sealed class SerializationBuffer : IDisposable
    {
        readonly MemoryStream _memoryStream;
        readonly BinarySerializationReader _reader;
        readonly BinarySerializationWriter _writer;

        bool _hasDisposed;
        State _state = State.Idle;

        public SerializationBuffer(SerializerRegistry serializerManager)
        {
            _memoryStream = new MemoryStream(1024);
            _reader = new BinarySerializationReader(serializerManager);
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
                TrecsDebugAssert.That(_state == State.Idle);
                return _memoryStream;
            }
        }

        public BinarySerializationWriter Writer
        {
            get
            {
                TrecsDebugAssert.That(_state == State.Writing);
                return _writer;
            }
        }

        public BinarySerializationReader Reader
        {
            get
            {
                TrecsDebugAssert.That(_state == State.Reading);
                return _reader;
            }
        }

        public long MemoryPosition
        {
            get
            {
                TrecsDebugAssert.That(_state == State.Idle);
                return _memoryStream.Position;
            }
        }

        public long MemoryLength
        {
            get
            {
                TrecsDebugAssert.That(_state == State.Idle);
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
            TrecsDebugAssert.That(_state == State.Idle);

            TrecsDebugAssert.That(allowUnclearedMemory || MemoryIsCleared);

            try
            {
                _writer.Start(
                    version: version,
                    includeTypeChecks: includeTypeChecks,
                    flags: flags,
                    enableMemoryTracking: enableMemoryTracking
                );
                _writer.WriteDelta("Value", value, baseValue);
                _writer.Complete(_memoryStream);
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
            TrecsDebugAssert.That(_state == State.Idle);

            TrecsDebugAssert.That(MemoryPosition == 0);

            try
            {
                _reader.Start(GetMemoryStreamAsReadOnlyMemory());
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
            TrecsDebugAssert.That(_state == State.Reading);
            return _reader.Read<T>(path);
        }

        public void Write<T>(string path, in T value)
        {
            TrecsDebugAssert.That(_state == State.Writing);
            _writer.Write(path, value);
        }

        public void StartWrite(
            int version,
            bool includeTypeChecks,
            long flags = 0,
            bool enableMemoryTracking = false
        )
        {
            TrecsDebugAssert.That(_state == State.Idle);

            TrecsDebugAssert.That(MemoryPosition == 0);

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
            TrecsDebugAssert.That(_state == State.Idle);
            _memoryStream.Position = 0;
        }

        public void ClearMemoryStream()
        {
            TrecsDebugAssert.That(_state == State.Idle);
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

        ReadOnlyMemory<byte> GetMemoryStreamAsReadOnlyMemory()
        {
            return new ReadOnlyMemory<byte>(
                _memoryStream.GetBuffer(),
                0,
                (int)_memoryStream.Length
            );
        }

        public void StartRead()
        {
            TrecsDebugAssert.That(_state == State.Idle);

            TrecsDebugAssert.That(MemoryPosition == 0);

            _state = State.Reading;
            _reader.Start(GetMemoryStreamAsReadOnlyMemory());
        }

        /// <summary>
        /// Read just the header at the current memory position without starting
        /// a full read. Does not advance the memory position. Intended for
        /// pre-flight validation (e.g. asserting required flags before calling
        /// <see cref="StartRead"/>).
        /// </summary>
        public PayloadHeader PeekHeader()
        {
            TrecsDebugAssert.That(_state == State.Idle);
            return PayloadHeader.Peek(_memoryStream);
        }

        public void StopRead(bool verifySentinel)
        {
            TrecsDebugAssert.That(_state == State.Reading);

            _state = State.Idle;
            _reader.Stop(verifySentinel: verifySentinel);
        }

        public long EndWrite()
        {
            TrecsDebugAssert.That(_state == State.Writing);
            _state = State.Idle;

            _writer.Complete(_memoryStream);

            var totalBytes = _memoryStream.Position;
            return totalBytes;
        }

        public void SaveMemoryToFile(string path)
        {
            TrecsDebugAssert.That(_state == State.Idle);

            TrecsDebugAssert.That(MemoryPosition == 0);

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
            TrecsDebugAssert.That(_state == State.Idle);

            TrecsDebugAssert.That(allowUnclearedMemory || MemoryIsCleared);

            try
            {
                _writer.Start(
                    version: version,
                    includeTypeChecks: includeTypeChecks,
                    flags: flags,
                    enableMemoryTracking: enableMemoryTracking
                );
                _writer.Write("Value", value);
                _writer.Complete(_memoryStream);
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
            TrecsDebugAssert.That(_state == State.Idle);

            TrecsDebugAssert.That(MemoryPosition == 0);

            try
            {
                _reader.Start(GetMemoryStreamAsReadOnlyMemory());
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
            TrecsDebugAssert.That(_state == State.Idle);

            TrecsDebugAssert.That(MemoryIsCleared);

            try
            {
                _writer.Start(
                    version: version,
                    includeTypeChecks: includeTypeChecks,
                    flags,
                    enableMemoryTracking: enableMemoryTracking
                );
                _writer.WriteObject("Value", value);
                _writer.Complete(_memoryStream);
            }
            catch
            {
                ResetForErrorRecovery();
                throw;
            }

            return _memoryStream.Position;
        }

        /// <summary>
        /// Compute a 64-bit xxHash checksum over the currently-written bytes.
        /// Safely handles the <see cref="MemoryStream.GetBuffer"/> /
        /// <see cref="MemoryStream.Length"/> discipline so callers cannot
        /// accidentally hash uninitialized trailing bytes (which would
        /// produce a non-deterministic checksum and break replay / desync
        /// detection).
        /// </summary>
        public ulong ComputeChecksum()
        {
            TrecsDebugAssert.That(!_hasDisposed);
            TrecsDebugAssert.That(_state == State.Idle);

            int length = (int)_memoryStream.Length;
            byte[] buffer = _memoryStream.GetBuffer();

            return CollisionResistantHashCalculator.ComputeXxHash64(buffer, length);
        }

        /// <summary>
        /// Note that you can't call this multiple times for different parts of same data
        /// </summary>
        public T ReadAll<T>(bool allowNonZeroMemoryPosition = false)
        {
            TrecsDebugAssert.That(_state == State.Idle);

            TrecsDebugAssert.That(allowNonZeroMemoryPosition || MemoryPosition == 0);

            try
            {
                _reader.Start(GetMemoryStreamAsReadOnlyMemory());
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
            TrecsDebugAssert.That(_state == State.Idle);

            TrecsDebugAssert.That(allowNonZeroMemoryPosition || MemoryPosition == 0);

            try
            {
                _reader.Start(GetMemoryStreamAsReadOnlyMemory());
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
            TrecsDebugAssert.That(_state == State.Idle);

            TrecsDebugAssert.That(MemoryIsCleared);

            try
            {
                _writer.Start(
                    version: version,
                    includeTypeChecks: includeTypeChecks,
                    flags,
                    enableMemoryTracking: enableMemoryTracking
                );
                _writer.WriteObjectDelta("Value", value, baseValue);
                _writer.Complete(_memoryStream);
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
            TrecsDebugAssert.That(_state == State.Idle);

            TrecsDebugAssert.That(MemoryIsCleared);

            _memoryStream.Write(bytes, 0, length);
        }

        public void LoadMemoryStreamFromArraySegment(ArraySegment<byte> segment, int length)
        {
            TrecsDebugAssert.That(_state == State.Idle);

            TrecsDebugAssert.That(MemoryIsCleared);

            _memoryStream.Write(segment.Array, segment.Offset, length);
        }

        public void LoadMemoryFromFile(string path)
        {
            TrecsDebugAssert.That(_state == State.Idle);
            TrecsDebugAssert.That(MemoryIsCleared);

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
            TrecsDebugAssert.That(_state == State.Idle);

            TrecsDebugAssert.That(allowNonZeroMemoryPosition || MemoryPosition == 0);

            try
            {
                _reader.Start(GetMemoryStreamAsReadOnlyMemory());
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
            TrecsDebugAssert.That(
                _state == State.Idle,
                "Expected state to be Idle but was {0}",
                _state
            );
            TrecsDebugAssert.That(!_hasDisposed);
            _hasDisposed = true;

            _memoryStream.Dispose();
        }

        public void BlitRead<T>(string name, ref T value)
            where T : unmanaged
        {
            TrecsDebugAssert.That(_state == State.Reading);
            _reader.BlitRead<T>(name, ref value);
        }

        public void BlitReadArray<T>(string name, T[] buffer, int count)
            where T : unmanaged
        {
            TrecsDebugAssert.That(_state == State.Reading);
            _reader.BlitReadArray<T>(name, buffer, count);
        }

        public unsafe void BlitReadArrayPtr<T>(string name, T* value, int length)
            where T : unmanaged
        {
            TrecsDebugAssert.That(_state == State.Reading);
            _reader.BlitReadArrayPtr<T>(name, value, length);
        }

        public bool ReadBit()
        {
            TrecsDebugAssert.That(_state == State.Reading);
            return _reader.ReadBit();
        }

        public void Read<T>(string name, ref T value)
        {
            TrecsDebugAssert.That(_state == State.Reading);
            _reader.Read(name, ref value);
        }

        public void ReadObject(string name, ref object value)
        {
            TrecsDebugAssert.That(_state == State.Reading);
            _reader.ReadObject(name, ref value);
        }

        public string ReadString(string name)
        {
            TrecsDebugAssert.That(_state == State.Reading);
            return _reader.ReadString(name);
        }

        public Type ReadTypeId(string name)
        {
            TrecsDebugAssert.That(_state == State.Reading);
            return _reader.ReadTypeId(name);
        }

        public void BlitReadDelta<T>(string name, ref T value, in T baseValue)
            where T : unmanaged
        {
            TrecsDebugAssert.That(_state == State.Reading);
            _reader.BlitReadDelta<T>(name, ref value, baseValue);
        }

        public void ReadDelta<T>(string name, ref T value, in T baseValue)
        {
            TrecsDebugAssert.That(_state == State.Reading);
            _reader.ReadDelta(name, ref value, baseValue);
        }

        public void ReadObjectDelta(string name, ref object value, object baseValue)
        {
            TrecsDebugAssert.That(_state == State.Reading);
            _reader.ReadObjectDelta(name, ref value, baseValue);
        }

        public string ReadStringDelta(string name, string baseValue)
        {
            TrecsDebugAssert.That(_state == State.Reading);
            return _reader.ReadStringDelta(name, baseValue);
        }

        public void WriteBit(bool value)
        {
            TrecsDebugAssert.That(_state == State.Writing);
            _writer.WriteBit(value);
        }

        public void WriteObject(string name, object value)
        {
            TrecsDebugAssert.That(_state == State.Writing);
            _writer.WriteObject(name, value);
        }

        public void WriteString(string name, string value)
        {
            TrecsDebugAssert.That(_state == State.Writing);
            _writer.WriteString(name, value);
        }

        public void BlitWrite<T>(string name, in T value)
            where T : unmanaged
        {
            TrecsDebugAssert.That(_state == State.Writing);
            _writer.BlitWrite(name, value);
        }

        public unsafe void BlitWriteArrayPtr<T>(string name, T* value, int length)
            where T : unmanaged
        {
            TrecsDebugAssert.That(_state == State.Writing);
            _writer.BlitWriteArrayPtr(name, value, length);
        }

        public void BlitWriteArray<T>(string name, T[] value, int count)
            where T : unmanaged
        {
            TrecsDebugAssert.That(_state == State.Writing);
            _writer.BlitWriteArray(name, value, count);
        }

        public void WriteTypeId(string name, Type type)
        {
            TrecsDebugAssert.That(_state == State.Writing);
            _writer.WriteTypeId(name, type);
        }

        public void WriteDelta<T>(string name, in T value, in T baseValue)
        {
            TrecsDebugAssert.That(_state == State.Writing);
            _writer.WriteDelta(name, value, baseValue);
        }

        public void WriteObjectDelta(string name, object value, object baseValue)
        {
            TrecsDebugAssert.That(_state == State.Writing);
            _writer.WriteObjectDelta(name, value, baseValue);
        }

        public void WriteStringDelta(string name, string value, string baseValue)
        {
            TrecsDebugAssert.That(_state == State.Writing);
            _writer.WriteStringDelta(name, value, baseValue);
        }

        public string GetMemoryReport()
        {
            TrecsDebugAssert.That(_state == State.Idle);
            return _writer.GetMemoryReport();
        }

        public void BlitWriteDelta<T>(string name, in T value, in T baseValue)
            where T : unmanaged
        {
            TrecsDebugAssert.That(_state == State.Writing);
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

        public long Flags
        {
            get
            {
                if (_state == State.Reading)
                {
                    return _reader.Flags;
                }
                else if (_state == State.Writing)
                {
                    return _writer.Flags;
                }
                throw new InvalidOperationException(
                    "Cannot access Flags when not reading or writing."
                );
            }
        }

        public int Version
        {
            get
            {
                if (_state == State.Reading)
                {
                    return _reader.Version;
                }
                else if (_state == State.Writing)
                {
                    return _writer.Version;
                }
                throw new InvalidOperationException(
                    "Cannot access Version when not reading or writing."
                );
            }
        }

        public long NumBytesWritten
        {
            get
            {
                TrecsDebugAssert.That(_state == State.Writing);
                return _writer.NumBytesWritten;
            }
        }

        public void WriteBytes(string name, byte[] buffer, int offset, int count)
        {
            TrecsDebugAssert.That(_state == State.Writing);
            _writer.WriteBytes(name, buffer, offset, count);
        }

        public int ReadBytes(string name, ref byte[] buffer)
        {
            TrecsDebugAssert.That(_state == State.Reading);
            return _reader.ReadBytes(name, ref buffer);
        }

        public unsafe void BlitWriteRawBytes(string name, void* ptr, int numBytes)
        {
            TrecsDebugAssert.That(_state == State.Writing);
            _writer.BlitWriteRawBytes(name, ptr, numBytes);
        }

        public unsafe void BlitReadRawBytes(string name, void* ptr, int numBytes)
        {
            TrecsDebugAssert.That(_state == State.Reading);
            _reader.BlitReadRawBytes(name, ptr, numBytes);
        }

        // Raw primitive read/write for callers that need to write unframed
        // data directly into the underlying stream (e.g. network message
        // headers). Only available when the buffer is Idle.
        public void WriteRawInt32(int value)
        {
            TrecsDebugAssert.That(_state == State.Idle);
            Span<byte> buf = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(buf, value);
            _memoryStream.Write(buf);
        }

        public void WriteRawByte(byte value)
        {
            TrecsDebugAssert.That(_state == State.Idle);
            _memoryStream.WriteByte(value);
        }

        public void WriteRawBytes(byte[] buffer, int offset, int count)
        {
            TrecsDebugAssert.That(_state == State.Idle);
            _memoryStream.Write(buffer, offset, count);
        }

        public int ReadRawInt32()
        {
            TrecsDebugAssert.That(_state == State.Idle);
            Span<byte> buf = stackalloc byte[sizeof(int)];
            int bytesRead = _memoryStream.Read(buf);
            TrecsAssert.That(bytesRead == sizeof(int));
            return BinaryPrimitives.ReadInt32LittleEndian(buf);
        }

        public byte ReadRawByte()
        {
            TrecsDebugAssert.That(_state == State.Idle);
            int b = _memoryStream.ReadByte();
            TrecsAssert.That(b >= 0);
            return (byte)b;
        }

        public int ReadRawBytes(byte[] buffer, int offset, int count)
        {
            TrecsDebugAssert.That(_state == State.Idle);
            return _memoryStream.Read(buffer, offset, count);
        }

        public enum State
        {
            Idle,
            Reading,
            Writing,
        }
    }
}
