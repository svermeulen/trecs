using System.IO;
using NUnit.Framework;
using Trecs.Internal;

namespace Trecs.Tests
{
    [TestFixture]
    public class FlagsPersistenceTests
    {
        private SerializerRegistry _serializerRegistry;
        private SerializationHelper _helper;
        private SerializationData _data;

        const long TestFlagA = 1L << 4;
        const long TestFlagB = 1L << 5;

        [SetUp]
        public void SetUp()
        {
            _serializerRegistry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(_serializerRegistry);
            _helper = new SerializationHelper(_serializerRegistry);
            _data = new SerializationData();
        }

        [Test]
        public void Flags_AreRecoveredByReaderFromHeader()
        {
            const long writtenFlags = TestFlagA | TestFlagB;

            _helper.WriteAll<int>(
                _data,
                42,
                TestConstants.Version,
                includeTypeChecks: true,
                flags: writtenFlags
            );

            _helper.Reader.Start(_data);
            TrecsDebugAssert.IsEqual(_helper.Reader.Flags, writtenFlags);
            TrecsDebugAssert.That(_helper.Reader.HasFlag(TestFlagA));
            TrecsDebugAssert.That(_helper.Reader.HasFlag(TestFlagB));
            _helper.Reader.CompletePartial();
        }

        [Test]
        public void PeekHeader_ReturnsVersionAndFlagsWithoutConsumingStream()
        {
            const long writtenFlags = TestFlagA;
            const int writtenVersion = 7;

            _helper.WriteAll<int>(
                _data,
                123,
                writtenVersion,
                includeTypeChecks: false,
                flags: writtenFlags
            );

            // The header fields are recoverable from the payload without consuming it.
            TrecsDebugAssert.IsEqual(_data.Version, writtenVersion);
            TrecsDebugAssert.IsEqual(_data.Flags, writtenFlags);
            TrecsDebugAssert.That(_data.HasFlag(TestFlagA));
            TrecsDebugAssert.That(!_data.HasFlag(TestFlagB));

            // Reading the header did not consume anything — a full read still works.
            var value = _helper.ReadAll<int>(_data);
            TrecsDebugAssert.IsEqual(value, 123);
        }

        [Test]
        public void ZeroFlags_RoundTripCleanly()
        {
            _helper.WriteAll<int>(_data, 0, TestConstants.Version, includeTypeChecks: true);

            TrecsDebugAssert.IsEqual(_data.Flags, 0L);
            TrecsDebugAssert.That(!_data.HasFlag(TestFlagA));
        }

        [Test]
        public void ReservedBit_OnWrite_ThrowsWithHelpfulMessage()
        {
            // Bit 2 is reserved for future Trecs use and must not be used by app code.
            const long reservedBit = 1L << 2;

            var ex = Assert.Throws<TrecsException>(() =>
                _helper.WriteAll<int>(
                    _data,
                    0,
                    TestConstants.Version,
                    includeTypeChecks: true,
                    flags: reservedBit
                )
            );
            StringAssert.Contains("reserved for Trecs", ex.Message);
        }

        [Test]
        public void SerializationFlags_DefinedMaskMatchesDesyncFriendlyHeaps()
        {
            TrecsDebugAssert.IsEqual(
                SerializationFlags.AllDefinedMask,
                SerializationFlags.DesyncFriendlyHeaps
            );
        }

        [Test]
        public void PeekHeader_OnUnrelatedBytes_ThrowsAndLeavesStreamPositionUnchanged()
        {
            var stream = new MemoryStream(new byte[] { 0x00, 0x00, 0x00, 0x01, 0x00 });
            stream.Position = 0;

            Assert.Throws<SerializationException>(() => PayloadHeader.Peek(stream));
            TrecsDebugAssert.IsEqual(stream.Position, 0L);
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
                0,
            };
            var stream = new MemoryStream(bytes);

            var ex = Assert.Throws<SerializationException>(() => PayloadHeader.Peek(stream));
            StringAssert.Contains("format version", ex.Message);
            TrecsDebugAssert.IsEqual(stream.Position, 0L);
        }
    }
}
