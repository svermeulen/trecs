namespace Trecs
{
    internal class HeapIdCounter
    {
        uint _value;
        readonly uint _stride;

        internal HeapIdCounter(uint start, uint stride)
        {
            _value = start;
            _stride = stride;
        }

        internal uint Alloc()
        {
            var id = _value;
            _value += _stride;
            return id;
        }

        internal void AdvancePast(uint address)
        {
            if (_value <= address)
            {
                uint diff = address - _value;
                uint steps = (diff / _stride) + 1;
                _value += steps * _stride;
            }
        }

        /// <summary>
        /// Advances <see cref="Value"/> to be at least <paramref name="value"/>.
        /// No-op if <see cref="Value"/> is already greater. Used by frame-scoped
        /// heaps when deserializing on top of an already-running game (e.g. when
        /// BundlePlayer swaps the input queue contents mid-session): the running
        /// game's counter must not be clobbered by a smaller saved value.
        /// </summary>
        internal void EnsureAtLeast(uint value)
        {
            if (_value < value)
            {
                _value = value;
            }
        }

        internal uint Value
        {
            get => _value;
            set => _value = value;
        }
    }
}
