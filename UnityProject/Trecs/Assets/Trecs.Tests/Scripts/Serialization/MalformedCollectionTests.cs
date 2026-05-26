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
        private SerializationBuffer _cacheHelper;
        private SerializerRegistry _serializerRegistry;

        [SetUp]
        public void SetUp()
        {
            _serializerRegistry = TestSerializerInstaller.CreateTestRegistry();
            _cacheHelper = new SerializationBuffer(_serializerRegistry);
        }

        [TearDown]
        public void TearDown()
        {
            _cacheHelper?.Dispose();
            _cacheHelper = null;
        }

        [Test]
        public void Collection_TruncatedElementData_ThrowsException()
        {
            var originalList = new List<string> { "hello", "world", "test" };
            var flags = 0L;

            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(
                originalList,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var serializedData = _cacheHelper.MemoryStream.ToArray();

            // Truncate the data in the middle of string serialization
            var truncatedData = new byte[serializedData.Length - 5]; // Remove last 5 bytes
            Array.Copy(serializedData, truncatedData, truncatedData.Length);

            var newCacheHelper = new SerializationBuffer(_serializerRegistry);
            try
            {
                newCacheHelper.LoadMemoryStreamFromArraySegment(
                    new ArraySegment<byte>(truncatedData),
                    truncatedData.Length
                );
                TrecsDebugAssert.Throws<Exception>(() => newCacheHelper.ReadAll<List<string>>());
            }
            finally
            {
                newCacheHelper?.Dispose();
            }
        }

        [Test]
        public void Collection_ZeroCountWithData_HandlesGracefully()
        {
            var list1 = new List<int>() { 1, 2, 3 };

            var flags = 0L;

            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(list1, TestConstants.Version, includeTypeChecks: true, flags);
            var serializedData = _cacheHelper.MemoryStream.ToArray();

            {
                int offset = 0;
                ReadOnlySpan<byte> span = serializedData;
                var (version, _, includesTypeChecks) = SerializationHeaderUtil.ReadHeader(
                    span,
                    ref offset
                );

                BitReader bitReader = new();
                bitReader.Reset(span, ref offset);
                bitReader.Complete();

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

            var newCacheHelper = new SerializationBuffer(_serializerRegistry);
            try
            {
                newCacheHelper.LoadMemoryStreamFromArraySegment(
                    new ArraySegment<byte>(serializedData),
                    serializedData.Length
                );
                TrecsDebugAssert.Throws<Exception>(() => newCacheHelper.ReadAll<List<int>>());
            }
            finally
            {
                newCacheHelper?.Dispose();
            }
        }

        [Test]
        public void Collection_LargerCountValue_HandlesGracefully()
        {
            var list1 = new List<int>() { 42 };

            var flags = 0L;

            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(list1, TestConstants.Version, includeTypeChecks: true, flags);
            var serializedData = _cacheHelper.MemoryStream.ToArray();

            {
                int offset = 0;
                ReadOnlySpan<byte> span = serializedData;
                var (version, _, includesTypeChecks) = SerializationHeaderUtil.ReadHeader(
                    span,
                    ref offset
                );

                BitReader bitReader = new();
                bitReader.Reset(span, ref offset);
                bitReader.Complete();

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

            var newCacheHelper = new SerializationBuffer(_serializerRegistry);
            try
            {
                newCacheHelper.LoadMemoryStreamFromArraySegment(
                    new ArraySegment<byte>(serializedData),
                    serializedData.Length
                );
                TrecsDebugAssert.Throws<Exception>(() => newCacheHelper.ReadAll<List<int>>());
            }
            finally
            {
                newCacheHelper?.Dispose();
            }
        }
    }
}
