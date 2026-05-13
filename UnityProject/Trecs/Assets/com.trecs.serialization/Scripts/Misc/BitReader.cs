using System;
using System.ComponentModel;
using System.IO;
using Trecs.Internal;

namespace Trecs.Serialization.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class BitReader
    {
        byte[] _bytes;
        int _byteIndex = 0;
        int _bitPosition = 0;
        int _totalBits = 0;
        int _currentCapacity = 0;
        bool _hasStarted;

        public BitReader()
        {
            _bytes = new byte[0];
            _currentCapacity = 0;
        }

        public bool HasMoreBits
        {
            get
            {
                Assert.That(_hasStarted);
                return (_byteIndex * 8 + _bitPosition) < _totalBits;
            }
        }

        public int BitCount
        {
            get
            {
                Assert.That(_hasStarted);
                return _totalBits;
            }
        }

        void EnsureCapacity(int requiredCapacity)
        {
            if (requiredCapacity > _currentCapacity)
            {
                var newCapacity = Math.Max(
                    requiredCapacity,
                    _currentCapacity + (_currentCapacity / 2)
                );
                _bytes = new byte[newCapacity];
                _currentCapacity = newCapacity;
            }
        }

        public void Reset(BinaryReader reader)
        {
            Assert.That(!_hasStarted);
            _hasStarted = true;

            _totalBits = reader.ReadInt32();
            var byteCount = reader.ReadInt32();
            EnsureCapacity(byteCount);

            reader.Read(_bytes, 0, byteCount);

            _byteIndex = 0;
            _bitPosition = 0;
        }

        public void Complete()
        {
            Assert.That(_hasStarted);
            _hasStarted = false;
        }

        public void ResetForErrorRecovery()
        {
            _hasStarted = false;
            _byteIndex = 0;
            _bitPosition = 0;
            _totalBits = 0;
        }

        public bool ReadBit()
        {
            Assert.That(_hasStarted);

            if (_byteIndex >= _bytes.Length || (_byteIndex * 8 + _bitPosition) >= _totalBits)
            {
                var consumed = _byteIndex * 8 + _bitPosition;
                throw new SerializationException(
                    $"BitReader underrun at byte {_byteIndex}, bit {_bitPosition} ({consumed} of {_totalBits} bits consumed) — truncated or corrupt stream"
                );
            }

            var bit = (_bytes[_byteIndex] & (1 << _bitPosition)) != 0;
            _bitPosition++;

            if (_bitPosition == 8)
            {
                _byteIndex++;
                _bitPosition = 0;
            }

            return bit;
        }
    }
}
