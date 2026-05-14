using NUnit.Framework;
using Trecs.Internal;

namespace Trecs.Tests
{
    [TestFixture]
    public class WriteBytesTests
    {
        SerializerRegistry _serializerRegistry;
        SerializationBuffer _binary;

        [SetUp]
        public void SetUp()
        {
            _serializerRegistry = new SerializerRegistry();
            _binary = new SerializationBuffer(_serializerRegistry);
        }

        [TearDown]
        public void TearDown()
        {
            _binary?.Dispose();
        }

        void BinaryRoundTrip(
            byte[] payload,
            int offset,
            int count,
            out byte[] outBuffer,
            out int outCount
        )
        {
            _binary.ClearMemoryStream();
            _binary.StartWrite(TestConstants.Version, includeTypeChecks: true, 0L);
            _binary.WriteBytes("bytes", payload, offset, count);
            _binary.EndWrite();
            _binary.ResetMemoryPosition();

            _binary.StartRead();
            outBuffer = null;
            outCount = _binary.ReadBytes("bytes", ref outBuffer);
            _binary.StopRead(verifySentinel: true);
        }

        [Test]
        public void Binary_Empty()
        {
            BinaryRoundTrip(new byte[0], 0, 0, out var buf, out var count);
            TrecsAssert.That(count == 0);
            TrecsAssert.That(buf != null);
        }

        [Test]
        public void Binary_SmallArray()
        {
            var data = new byte[] { 0x42, 0xFF, 0x00, 0x7F };
            BinaryRoundTrip(data, 0, data.Length, out var buf, out var count);
            TrecsAssert.That(count == 4);
            for (int i = 0; i < 4; i++)
                TrecsAssert.That(buf[i] == data[i]);
        }

        [Test]
        public void Binary_Large1KB()
        {
            var data = new byte[1024];
            for (int i = 0; i < data.Length; i++)
                data[i] = (byte)(i % 256);
            BinaryRoundTrip(data, 0, data.Length, out var buf, out var count);
            TrecsAssert.That(count == 1024);
            for (int i = 0; i < 1024; i++)
                TrecsAssert.That(buf[i] == data[i]);
        }

        [Test]
        public void Binary_OffsetAndCount_OnlyWritesRequestedRange()
        {
            // 100-byte buffer; only bytes [10, 20) are meaningful
            var buffer = new byte[100];
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = 0xAA;
            for (int i = 10; i < 20; i++)
                buffer[i] = (byte)(i - 10 + 1); // 1..10

            BinaryRoundTrip(buffer, offset: 10, count: 10, out var buf, out var count);
            TrecsAssert.That(count == 10);
            for (int i = 0; i < 10; i++)
                TrecsAssert.That(buf[i] == (byte)(i + 1));
        }

        [Test]
        public void Binary_ReusesExistingBufferWhenLargeEnough()
        {
            var data = new byte[] { 1, 2, 3 };

            _binary.ClearMemoryStream();
            _binary.StartWrite(TestConstants.Version, includeTypeChecks: true, 0L);
            _binary.WriteBytes("bytes", data, 0, data.Length);
            _binary.EndWrite();
            _binary.ResetMemoryPosition();

            var preallocated = new byte[16];
            _binary.StartRead();
            var buf = preallocated;
            int count = _binary.ReadBytes("bytes", ref buf);
            _binary.StopRead(verifySentinel: true);

            TrecsAssert.That(count == 3);
            TrecsAssert.That(
                ReferenceEquals(buf, preallocated),
                "Expected caller buffer to be reused when large enough"
            );
        }
    }
}
