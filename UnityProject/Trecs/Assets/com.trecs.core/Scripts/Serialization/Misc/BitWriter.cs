using System.Buffers;
using System.ComponentModel;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal sealed class BitWriter
    {
        // Bits are packed into whole bytes appended to this externally-owned buffer — the
        // active SerializationData's bit-field section — so a serialized payload can be
        // retained without copying the bit-field bytes out of the writer. The
        // [totalBits][byteCount] length prefix is NOT stored here; it is synthesized only
        // when SerializationData emits the contiguous wire form.
        ArrayBufferWriter<byte> _target;
        byte _currentByte = 0;
        int _bitPosition = 0;
        int _totalBits = 0;
        bool _hasStarted;

        public BitWriter() { }

        public int BitCount => _totalBits;

        public void Start(ArrayBufferWriter<byte> target)
        {
            TrecsDebugAssert.That(!_hasStarted);
            _hasStarted = true;

            _target = target;
            _currentByte = 0;
            _bitPosition = 0;
            _totalBits = 0;
        }

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
                AppendByte(_currentByte);
                _currentByte = 0;
                _bitPosition = 0;
            }
        }

        /// <summary>
        /// Flush the trailing partial byte into the target buffer so it holds every packed
        /// bit, leaving <see cref="BitCount"/> final. Used by the SerializationData write
        /// path, where the section bytes stay in place (no contiguous emit).
        /// </summary>
        public void Complete()
        {
            TrecsDebugAssert.That(_hasStarted);
            _hasStarted = false;
            FlushPartialByte();
        }

        public void ResetForErrorRecovery()
        {
            _hasStarted = false;
            _currentByte = 0;
            _bitPosition = 0;
            _totalBits = 0;
            // _target's bytes are cleared by its owner (SerializationData.Clear, via the
            // writer's Start / error-recovery), so nothing to clear here.
        }

        void FlushPartialByte()
        {
            if (_bitPosition > 0)
            {
                AppendByte(_currentByte);
            }
        }

        void AppendByte(byte b)
        {
            var span = _target.GetSpan(1);
            span[0] = b;
            _target.Advance(1);
        }
    }
}
