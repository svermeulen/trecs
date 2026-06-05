using NUnit.Framework;
using Trecs.Internal;

namespace Trecs.Tests
{
    [TestFixture]
    public class WriteBytesTests
    {
        SerializerRegistry _serializerRegistry;
        SerializationHelper _helper;
        SerializationData _data;

        [SetUp]
        public void SetUp()
        {
            _serializerRegistry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(_serializerRegistry);
            _helper = new SerializationHelper(_serializerRegistry);
            _data = new SerializationData();
        }

        void BinaryRoundTrip(
            byte[] payload,
            int offset,
            int count,
            out byte[] outBuffer,
            out int outCount
        )
        {
            _helper.Writer.Start(
                _data,
                version: TestConstants.Version,
                includeTypeChecks: true,
                flags: 0L
            );
            _helper.Writer.WriteBytes("bytes", payload, offset, count);
            _helper.Writer.Complete();
            _helper.Reader.Start(_data);
            outBuffer = null;
            outCount = _helper.Reader.ReadBytes("bytes", ref outBuffer);
            _helper.Reader.Complete();
        }

        [Test]
        public void Binary_Empty()
        {
            BinaryRoundTrip(new byte[0], 0, 0, out var buf, out var count);
            TrecsDebugAssert.That(count == 0);
            TrecsDebugAssert.That(buf != null);
        }

        [Test]
        public void Binary_SmallArray()
        {
            var data = new byte[] { 0x42, 0xFF, 0x00, 0x7F };
            BinaryRoundTrip(data, 0, data.Length, out var buf, out var count);
            TrecsDebugAssert.That(count == 4);
            for (int i = 0; i < 4; i++)
                TrecsDebugAssert.That(buf[i] == data[i]);
        }

        [Test]
        public void Binary_Large1KB()
        {
            var data = new byte[1024];
            for (int i = 0; i < data.Length; i++)
                data[i] = (byte)(i % 256);
            BinaryRoundTrip(data, 0, data.Length, out var buf, out var count);
            TrecsDebugAssert.That(count == 1024);
            for (int i = 0; i < 1024; i++)
                TrecsDebugAssert.That(buf[i] == data[i]);
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
            TrecsDebugAssert.That(count == 10);
            for (int i = 0; i < 10; i++)
                TrecsDebugAssert.That(buf[i] == (byte)(i + 1));
        }

        [Test]
        public void Binary_ReusesExistingBufferWhenLargeEnough()
        {
            var data = new byte[] { 1, 2, 3 };

            _helper.Writer.Start(
                _data,
                version: TestConstants.Version,
                includeTypeChecks: true,
                flags: 0L
            );
            _helper.Writer.WriteBytes("bytes", data, 0, data.Length);
            _helper.Writer.Complete();

            var preallocated = new byte[16];
            _helper.Reader.Start(_data);
            var buf = preallocated;
            int count = _helper.Reader.ReadBytes("bytes", ref buf);
            _helper.Reader.Complete();

            TrecsDebugAssert.That(count == 3);
            TrecsDebugAssert.That(
                ReferenceEquals(buf, preallocated),
                "Expected caller buffer to be reused when large enough"
            );
        }
    }
}
