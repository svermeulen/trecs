using System.IO;
using NUnit.Framework;
using Trecs.Internal;

namespace Trecs.Tests
{
    [TestFixture]
    public class FlagsPersistenceTests
    {
        private SerializerRegistry _serializerRegistry;
        private SerializationBuffer _buffer;

        const long TestFlagA = 1L << 4;
        const long TestFlagB = 1L << 5;

        [SetUp]
        public void SetUp()
        {
            _serializerRegistry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(_serializerRegistry);
            _buffer = new SerializationBuffer(_serializerRegistry);
        }

        [TearDown]
        public void TearDown()
        {
            _buffer?.Dispose();
        }

        [Test]
        public void Flags_AreRecoveredByReaderFromHeader()
        {
            const long writtenFlags = TestFlagA | TestFlagB;

            _buffer.ClearMemoryStream();
            _buffer.WriteAll<int>(
                42,
                TestConstants.Version,
                includeTypeChecks: true,
                flags: writtenFlags
            );
            _buffer.ResetMemoryPosition();

            _buffer.StartRead();
            TrecsAssert.IsEqual(((ISerializationReader)_buffer).Flags, writtenFlags);
            TrecsAssert.That(_buffer.HasFlag(TestFlagA));
            TrecsAssert.That(_buffer.HasFlag(TestFlagB));
            _buffer.StopRead(verifySentinel: false);
        }

        [Test]
        public void PeekHeader_ReturnsVersionAndFlagsWithoutConsumingStream()
        {
            const long writtenFlags = TestFlagA;
            const int writtenVersion = 7;

            _buffer.ClearMemoryStream();
            _buffer.WriteAll<int>(
                123,
                writtenVersion,
                includeTypeChecks: false,
                flags: writtenFlags
            );
            _buffer.ResetMemoryPosition();

            var header = _buffer.PeekHeader();
            TrecsAssert.IsEqual(header.Version, writtenVersion);
            TrecsAssert.IsEqual(header.Flags, writtenFlags);
            TrecsAssert.That(header.HasFlag(TestFlagA));
            TrecsAssert.That(!header.HasFlag(TestFlagB));

            // Stream position is unchanged — we can still perform a full read.
            var value = _buffer.ReadAll<int>();
            TrecsAssert.IsEqual(value, 123);
        }

        [Test]
        public void ZeroFlags_RoundTripCleanly()
        {
            _buffer.ClearMemoryStream();
            _buffer.WriteAll<int>(0, TestConstants.Version, includeTypeChecks: true);
            _buffer.ResetMemoryPosition();

            var header = _buffer.PeekHeader();
            TrecsAssert.IsEqual(header.Flags, 0L);
            TrecsAssert.That(!header.HasFlag(TestFlagA));
        }

        [Test]
        public void ReservedBit_OnWrite_ThrowsWithHelpfulMessage()
        {
            // Bit 2 is reserved for future Trecs use and must not be used by app code.
            const long reservedBit = 1L << 2;

            _buffer.ClearMemoryStream();
            var ex = Assert.Throws<TrecsException>(() =>
                _buffer.WriteAll<int>(
                    0,
                    TestConstants.Version,
                    includeTypeChecks: true,
                    flags: reservedBit
                )
            );
            StringAssert.Contains("reserved for Trecs", ex.Message);
        }

        [Test]
        public void TrecsIsForChecksum_Allowed_DoesNotTripReservedAssertion()
        {
            // IsForChecksum lives in the reserved range but is an officially-defined
            // Trecs flag so passing it must not trip the reserved-bit check.
            _buffer.ClearMemoryStream();
            _buffer.WriteAll<int>(
                0,
                TestConstants.Version,
                includeTypeChecks: true,
                flags: SerializationFlags.IsForChecksum
            );
            _buffer.ResetMemoryPosition();
            var header = _buffer.PeekHeader();
            TrecsAssert.That(header.HasFlag(SerializationFlags.IsForChecksum));
        }

        [Test]
        public void PeekHeader_OnUnrelatedBytes_ThrowsAndLeavesStreamPositionUnchanged()
        {
            var stream = new MemoryStream(new byte[] { 0x00, 0x00, 0x00, 0x01, 0x00 });
            stream.Position = 0;

            Assert.Throws<TrecsException>(() => PayloadHeader.Peek(stream));
            TrecsAssert.IsEqual(stream.Position, 0L);
        }

        [Test]
        public void PeekHeader_OnUnsupportedFormatVersion_Throws()
        {
            // T R <bogus-format-version> ...
            var bytes = new byte[]
            {
                (byte)'T',
                (byte)'R',
                0xFF,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
            };
            var stream = new MemoryStream(bytes);

            var ex = Assert.Throws<TrecsException>(() => PayloadHeader.Peek(stream));
            StringAssert.Contains("format version", ex.Message);
            TrecsAssert.IsEqual(stream.Position, 0L);
        }
    }
}
