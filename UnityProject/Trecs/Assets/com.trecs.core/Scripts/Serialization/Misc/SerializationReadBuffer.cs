using System;
using System.IO;

namespace Trecs.Internal
{
    /// <summary>
    /// Read-side counterpart to <see cref="SerializationData"/>: turns a loaded contiguous payload —
    /// bytes already in memory (<see cref="Wrap"/>), or a <see cref="Stream"/> that must be drained
    /// first (<see cref="Load"/>) — into the <see cref="View"/> a
    /// <see cref="BinarySerializationReader"/> / <see cref="SerializationHelper"/> can consume, with
    /// no copy of the data section. Reused across loads (both the drain buffer and the view are
    /// kept), so a holder pays no per-load allocation. Main-thread only.
    ///
    /// <para>This is the read half of the retired <c>SerializationBuffer</c> convenience; the write
    /// half is just <see cref="SerializationData"/>, which a writer fills directly.</para>
    /// </summary>
    public sealed class SerializationReadBuffer
    {
        const int DefaultCapacity = 4 * 1024;

        readonly ContiguousSerializationData _view = new();
        byte[] _drain = Array.Empty<byte>();
        bool _populated;

        /// <summary>
        /// The buffer's read view over the most recently <see cref="Wrap"/>ped / <see cref="Load"/>ed
        /// payload — the same reused instance every time, repointed by each call, so it is valid
        /// only until the next <see cref="Wrap"/>/<see cref="Load"/> (and, for <see cref="Wrap"/>,
        /// only while the wrapped bytes stay alive). Asserts that the buffer has been populated.
        /// </summary>
        public IReadOnlySerializationData View
        {
            get
            {
                TrecsAssert.That(
                    _populated,
                    "SerializationReadBuffer.View read before any Wrap/Load call"
                );
                return _view;
            }
        }

        /// <summary>
        /// Point <see cref="View"/> at contiguous <paramref name="payload"/> bytes already in
        /// memory — zero copy (the bytes are sliced in place, so keep them alive while the reader
        /// runs). Use this when the bytes are already in hand; for a stream prefer
        /// <see cref="Load"/>.
        /// </summary>
        public IReadOnlySerializationData Wrap(ReadOnlyMemory<byte> payload)
        {
            _view.Wrap(payload);
            _populated = true;
            return _view;
        }

        /// <summary>
        /// Drain <paramref name="source"/> from its current position to the end into the reused
        /// buffer, then point <see cref="View"/> at it. Handles both seekable streams (file / store
        /// streams — pre-sized from the known length) and forward-only streams (grown as read).
        /// </summary>
        public IReadOnlySerializationData Load(Stream source)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            int length;
            if (source.CanSeek)
            {
                int remaining = checked((int)(source.Length - source.Position));
                EnsureCapacity(remaining);
                length = FillExact(source, _drain, remaining);
            }
            else
            {
                length = 0;
                int read;
                do
                {
                    if (length == _drain.Length)
                    {
                        EnsureCapacity(_drain.Length == 0 ? DefaultCapacity : _drain.Length * 2);
                    }
                    read = source.Read(_drain, length, _drain.Length - length);
                    length += read;
                } while (read > 0);
            }

            return Wrap(new ReadOnlyMemory<byte>(_drain, 0, length));
        }

        void EnsureCapacity(int capacity)
        {
            if (_drain.Length < capacity)
            {
                _drain = new byte[capacity];
            }
        }

        // Reads exactly count bytes (or until EOF) into buffer; returns the count actually read.
        static int FillExact(Stream source, byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = source.Read(buffer, offset, count - offset);
                if (read <= 0)
                {
                    break;
                }
                offset += read;
            }
            return offset;
        }
    }
}
