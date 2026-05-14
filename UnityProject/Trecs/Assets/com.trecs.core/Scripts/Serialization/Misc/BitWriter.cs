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
            TrecsAssert.That(_hasStarted);

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

        public void Complete(BinaryWriter writer)
        {
            TrecsAssert.That(_hasStarted);
            _hasStarted = false;

            writer.Write(_totalBits);

            if (_bitPosition > 0)
            {
                _bytes.Add(_currentByte);
            }

            writer.Write(_bytes.Count);
            foreach (var b in _bytes)
            {
                writer.Write(b);
            }
        }

        public void Reset()
        {
            TrecsAssert.That(!_hasStarted);
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
