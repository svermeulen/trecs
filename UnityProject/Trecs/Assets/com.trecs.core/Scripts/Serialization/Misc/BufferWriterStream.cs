using System;
using System.Buffers;
using System.ComponentModel;
using System.IO;

namespace Trecs.Internal
{
    /// <summary>
    /// Thin write-only <see cref="Stream"/> adapter over an
    /// <see cref="IBufferWriter{T}"/> of bytes. Lets APIs that demand a
    /// <see cref="Stream"/> (notably <see cref="BinaryWriter"/>) write into
    /// an <see cref="ArrayBufferWriter{T}"/>-backed buffer without an
    /// intermediate <see cref="MemoryStream"/> hop and its growth/copy
    /// behaviour.
    ///
    /// Only the write surface is implemented — reads, seeks, length lookups,
    /// and explicit positioning all throw. <see cref="BinarySerializationWriter"/>
    /// only writes through this stream; the accumulated bytes are consumed
    /// directly off the underlying writer's <c>WrittenSpan</c>.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal sealed class BufferWriterStream : Stream
    {
        readonly IBufferWriter<byte> _writer;

        public BufferWriterStream(IBufferWriter<byte> writer)
        {
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;

        public override long Length =>
            throw new NotSupportedException(
                "BufferWriterStream is write-only; query the underlying IBufferWriter for length."
            );

        public override long Position
        {
            get =>
                throw new NotSupportedException(
                    "BufferWriterStream is write-only; query the underlying IBufferWriter for position."
                );
            set => throw new NotSupportedException("BufferWriterStream does not support seeking.");
        }

        public override void Flush()
        {
            // No-op: the underlying IBufferWriter has no buffering of its own
            // beyond what we Advance() in Write.
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("BufferWriterStream is write-only.");

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException("BufferWriterStream does not support seeking.");

        public override void SetLength(long value) =>
            throw new NotSupportedException("BufferWriterStream does not support SetLength.");

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if ((uint)offset > (uint)buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if ((uint)count > (uint)(buffer.Length - offset))
                throw new ArgumentOutOfRangeException(nameof(count));

            if (count == 0)
            {
                return;
            }

            var span = _writer.GetSpan(count);
            new ReadOnlySpan<byte>(buffer, offset, count).CopyTo(span);
            _writer.Advance(count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.IsEmpty)
            {
                return;
            }

            var span = _writer.GetSpan(buffer.Length);
            buffer.CopyTo(span);
            _writer.Advance(buffer.Length);
        }

        public override void WriteByte(byte value)
        {
            var span = _writer.GetSpan(1);
            span[0] = value;
            _writer.Advance(1);
        }
    }
}
