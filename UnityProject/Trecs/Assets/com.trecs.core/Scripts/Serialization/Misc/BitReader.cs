using System;
using System.ComponentModel;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal sealed class BitReader
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

        public int ConsumedBitCount
        {
            get
            {
                TrecsDebugAssert.That(_hasStarted);
                return _byteIndex * 8 + _bitPosition;
            }
        }

        public int BitCount
        {
            get
            {
                TrecsDebugAssert.That(_hasStarted);
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

        /// <summary>
        /// Reset from a standalone packed bit-field section plus its bit count — the form held by
        /// <see cref="IReadOnlySerializationData"/>. The section is already sliced out (the
        /// <c>[bitCount][bitFieldBytes][dataBytes]</c> prefix is parsed by the caller), so there
        /// is nothing to parse here.
        /// </summary>
        public void Reset(ReadOnlySpan<byte> packedBytes, int totalBits)
        {
            TrecsDebugAssert.That(!_hasStarted);
            _hasStarted = true;

            _totalBits = totalBits;
            EnsureCapacity(packedBytes.Length);
            packedBytes.CopyTo(_bytes);

            _byteIndex = 0;
            _bitPosition = 0;
        }

        public void Complete()
        {
            TrecsDebugAssert.That(_hasStarted);
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
            TrecsDebugAssert.That(_hasStarted);

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
