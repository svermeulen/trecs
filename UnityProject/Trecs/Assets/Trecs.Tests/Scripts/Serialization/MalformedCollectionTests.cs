using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
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

            using (var stream = new MemoryStream(serializedData))
            {
                var (version, _, includesTypeChecks) = SerializationHeaderUtil.ReadHeader(stream);

                BitReader bitReader = new();
                bitReader.Reset(stream);
                bitReader.Complete();

                if (includesTypeChecks)
                {
                    Span<byte> intBuf = stackalloc byte[sizeof(int)];

                    stream.Read(intBuf);
                    var listTypeId = BinaryPrimitives.ReadInt32LittleEndian(intBuf);
                    TrecsDebugAssert.IsEqual(listTypeId, TypeId<List<int>>.Value.Value);

                    stream.Read(intBuf);
                    var intTypeId = BinaryPrimitives.ReadInt32LittleEndian(intBuf);
                    TrecsDebugAssert.IsEqual(intTypeId, TypeId<int>.Value.Value);
                }

                // This is currently 3 but let's change to 0
                // This should cause the sentinel to not be in correct place
                Span<byte> corruptBuf = stackalloc byte[sizeof(int)];
                BinaryPrimitives.WriteInt32LittleEndian(corruptBuf, 0);
                stream.Write(corruptBuf);
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

            using (var stream = new MemoryStream(serializedData))
            {
                var (version, _, includesTypeChecks) = SerializationHeaderUtil.ReadHeader(stream);

                BitReader bitReader = new();
                bitReader.Reset(stream);
                bitReader.Complete();

                if (includesTypeChecks)
                {
                    Span<byte> intBuf = stackalloc byte[sizeof(int)];

                    stream.Read(intBuf);
                    var listTypeId = BinaryPrimitives.ReadInt32LittleEndian(intBuf);
                    TrecsDebugAssert.IsEqual(listTypeId, TypeId<List<int>>.Value.Value);

                    stream.Read(intBuf);
                    var intTypeId = BinaryPrimitives.ReadInt32LittleEndian(intBuf);
                    TrecsDebugAssert.IsEqual(intTypeId, TypeId<int>.Value.Value);
                }

                // This is currently 1 but let's change to 10
                Span<byte> corruptBuf = stackalloc byte[sizeof(int)];
                BinaryPrimitives.WriteInt32LittleEndian(corruptBuf, 10);
                stream.Write(corruptBuf);
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
