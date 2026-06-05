using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using NUnit.Framework;
using Trecs.Internal;

namespace Trecs.Tests
{
    [TestFixture]
    public class MalformedCollectionTests
    {
        private SerializationHelper _helper;
        private SerializationData _data;
        private SerializationReadBuffer _readBuffer;
        private SerializerRegistry _serializerRegistry;

        [SetUp]
        public void SetUp()
        {
            _serializerRegistry = TestSerializerInstaller.CreateTestRegistry();
            _helper = new SerializationHelper(_serializerRegistry);
            _data = new SerializationData();
            _readBuffer = new SerializationReadBuffer();
        }

        [Test]
        public void Collection_TruncatedElementData_ThrowsException()
        {
            var originalList = new List<string> { "hello", "world", "test" };
            var flags = 0L;

            _helper.WriteAll(
                _data,
                originalList,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var serializedData = _data.ToContiguousBytes();

            // Truncate the data in the middle of string serialization
            var truncatedData = new byte[serializedData.Length - 5]; // Remove last 5 bytes
            Array.Copy(serializedData, truncatedData, truncatedData.Length);

            TrecsDebugAssert.Throws<Exception>(() =>
                _helper.ReadAll<List<string>>(_readBuffer.Wrap(truncatedData))
            );
        }

        [Test]
        public void Collection_ZeroCountWithData_HandlesGracefully()
        {
            var list1 = new List<int>() { 1, 2, 3 };

            var flags = 0L;

            _helper.WriteAll(_data, list1, TestConstants.Version, includeTypeChecks: true, flags);
            var serializedData = _data.ToContiguousBytes();

            {
                int offset = 0;
                ReadOnlySpan<byte> span = serializedData;
                var (version, _, includesTypeChecks) = SerializationHeaderUtil.ReadHeader(
                    span,
                    ref offset
                );

                // Section length prefix: [bitCount][bitFieldByteCount][dataByteCount]; skip it and
                // the bit-field section to land at the start of the data section.
                offset += sizeof(int); // bitCount
                int bitFieldByteCount = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset));
                offset += sizeof(int) * 2; // bitFieldByteCount + dataByteCount
                offset += bitFieldByteCount;

                if (includesTypeChecks)
                {
                    var listTypeId = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset));
                    offset += sizeof(int);
                    TrecsDebugAssert.IsEqual(listTypeId, TypeId<List<int>>.Value.Value);

                    var intTypeId = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset));
                    offset += sizeof(int);
                    TrecsDebugAssert.IsEqual(intTypeId, TypeId<int>.Value.Value);
                }

                // This is currently 3 but let's change to 0
                // This should cause the sentinel to not be in correct place
                BinaryPrimitives.WriteInt32LittleEndian(
                    new Span<byte>(serializedData, offset, sizeof(int)),
                    0
                );
            }

            TrecsDebugAssert.Throws<Exception>(() =>
                _helper.ReadAll<List<int>>(_readBuffer.Wrap(serializedData))
            );
        }

        [Test]
        public void Collection_LargerCountValue_HandlesGracefully()
        {
            var list1 = new List<int>() { 42 };

            var flags = 0L;

            _helper.WriteAll(_data, list1, TestConstants.Version, includeTypeChecks: true, flags);
            var serializedData = _data.ToContiguousBytes();

            {
                int offset = 0;
                ReadOnlySpan<byte> span = serializedData;
                var (version, _, includesTypeChecks) = SerializationHeaderUtil.ReadHeader(
                    span,
                    ref offset
                );

                // Section length prefix: [bitCount][bitFieldByteCount][dataByteCount]; skip it and
                // the bit-field section to land at the start of the data section.
                offset += sizeof(int); // bitCount
                int bitFieldByteCount = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset));
                offset += sizeof(int) * 2; // bitFieldByteCount + dataByteCount
                offset += bitFieldByteCount;

                if (includesTypeChecks)
                {
                    var listTypeId = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset));
                    offset += sizeof(int);
                    TrecsDebugAssert.IsEqual(listTypeId, TypeId<List<int>>.Value.Value);

                    var intTypeId = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset));
                    offset += sizeof(int);
                    TrecsDebugAssert.IsEqual(intTypeId, TypeId<int>.Value.Value);
                }

                // This is currently 1 but let's change to 10
                BinaryPrimitives.WriteInt32LittleEndian(
                    new Span<byte>(serializedData, offset, sizeof(int)),
                    10
                );
            }

            TrecsDebugAssert.Throws<Exception>(() =>
                _helper.ReadAll<List<int>>(_readBuffer.Wrap(serializedData))
            );
        }
    }
}
