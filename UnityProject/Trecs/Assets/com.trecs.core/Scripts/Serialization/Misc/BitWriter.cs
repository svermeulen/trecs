using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal sealed class BitWriter
    {
        readonly List<byte> _bytes = new();
        byte _currentByte = 0;
        int _bitPosition = 0;
        int _totalBits = 0;
        bool _hasStarted;

        public BitWriter() { }

        public int BitCount => _totalBits;

        public void WriteBit(bool value)
        {
            TrecsDebugAssert.That(_hasStarted);

            if (value)
            {
                _currentByte |= (byte)(1 << _bitPosition);
            }
            _bitPosition++;
            _totalBits++;

            if (_bitPosition == 8)
            {
                _bytes.Add(_currentByte);
                _currentByte = 0;
                _bitPosition = 0;
            }
        }

        public int ComputeOutputSize()
        {
            int byteCount = _bytes.Count + (_bitPosition > 0 ? 1 : 0);
            return sizeof(int) * 2 + byteCount;
        }

        public void Complete(Stream stream)
        {
            TrecsDebugAssert.That(_hasStarted);
            _hasStarted = false;

            if (_bitPosition > 0)
            {
                _bytes.Add(_currentByte);
            }

            Span<byte> header = stackalloc byte[sizeof(int) * 2];
            BinaryPrimitives.WriteInt32LittleEndian(header, _totalBits);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(sizeof(int)), _bytes.Count);
            stream.Write(header);

            foreach (var b in _bytes)
            {
                stream.WriteByte(b);
            }
        }

        public void CompleteTo(Span<byte> dest, ref int offset)
        {
            TrecsDebugAssert.That(_hasStarted);
            _hasStarted = false;

            if (_bitPosition > 0)
            {
                _bytes.Add(_currentByte);
            }

            BinaryPrimitives.WriteInt32LittleEndian(dest.Slice(offset), _totalBits);
            offset += sizeof(int);
            BinaryPrimitives.WriteInt32LittleEndian(dest.Slice(offset), _bytes.Count);
            offset += sizeof(int);

            for (int i = 0; i < _bytes.Count; i++)
            {
                dest[offset++] = _bytes[i];
            }
        }

        public void Reset()
        {
            TrecsDebugAssert.That(!_hasStarted);
            _hasStarted = true;

            _bytes.Clear();
            _currentByte = 0;
            _bitPosition = 0;
            _totalBits = 0;
        }

        public void ResetForErrorRecovery()
        {
            _hasStarted = false;
            _bytes.Clear();
            _currentByte = 0;
            _bitPosition = 0;
            _totalBits = 0;
        }
    }
}
