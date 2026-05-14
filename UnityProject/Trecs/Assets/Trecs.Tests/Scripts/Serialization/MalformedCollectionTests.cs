using System;
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
                TrecsAssert.Throws<Exception>(() => newCacheHelper.ReadAll<List<string>>());
            }
            finally
            {
                newCacheHelper?.Dispose();
            }
        }

        [Test]
        public void Dictionary_MissingValueData_ThrowsException()
        {
            var originalDict = new Dictionary<int, string> { { 1, "one" }, { 2, "two" } };
            var flags = 0L;

            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(
                originalDict,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var serializedData = _cacheHelper.MemoryStream.ToArray();

            // Truncate after the keys but before all values are serialized
            var truncatedData = new byte[serializedData.Length - 8]; // Remove some bytes
            Array.Copy(serializedData, truncatedData, truncatedData.Length);

            var newCacheHelper = new SerializationBuffer(_serializerRegistry);
            try
            {
                newCacheHelper.LoadMemoryStreamFromArraySegment(
                    new ArraySegment<byte>(truncatedData),
                    truncatedData.Length
                );
                TrecsAssert.Throws<Exception>(() =>
                    newCacheHelper.ReadAll<Dictionary<int, string>>()
                );
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
            using (var reader = new BinaryReader(stream))
            {
                var (version, _, includesTypeChecks) = SerializationHeaderUtil.ReadHeader(reader);

                BitReader bitReader = new();
                bitReader.Reset(reader);
                bitReader.Complete();

                if (includesTypeChecks)
                {
                    var listTypeId = reader.ReadInt32(); // list<> type id
                    TrecsAssert.IsEqual(listTypeId, TypeIdProvider.GetTypeId<List<int>>());

                    var intTypeId = reader.ReadInt32();
                    TrecsAssert.IsEqual(intTypeId, TypeIdProvider.GetTypeId<int>());
                }

                using (var writer = new BinaryWriter(stream))
                {
                    // This is currently 3 but let's change to 0
                    // This should cause the sentinel to not be in correct place
                    writer.Write(0);
                }
            }

            var newCacheHelper = new SerializationBuffer(_serializerRegistry);
            try
            {
                newCacheHelper.LoadMemoryStreamFromArraySegment(
                    new ArraySegment<byte>(serializedData),
                    serializedData.Length
                );
                TrecsAssert.Throws<Exception>(() => newCacheHelper.ReadAll<List<int>>());
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
            using (var reader = new BinaryReader(stream))
            {
                var (version, _, includesTypeChecks) = SerializationHeaderUtil.ReadHeader(reader);

                BitReader bitReader = new();
                bitReader.Reset(reader);
                bitReader.Complete();

                if (includesTypeChecks)
                {
                    var listTypeId = reader.ReadInt32(); // list<> type id
                    TrecsAssert.IsEqual(listTypeId, TypeIdProvider.GetTypeId<List<int>>());

                    var intTypeId = reader.ReadInt32();
                    TrecsAssert.IsEqual(intTypeId, TypeIdProvider.GetTypeId<int>());
                }

                using (var writer = new BinaryWriter(stream))
                {
                    // This is currently 1 but let's change to 10
                    writer.Write(10);
                }
            }

            var newCacheHelper = new SerializationBuffer(_serializerRegistry);
            try
            {
                newCacheHelper.LoadMemoryStreamFromArraySegment(
                    new ArraySegment<byte>(serializedData),
                    serializedData.Length
                );
                TrecsAssert.Throws<Exception>(() => newCacheHelper.ReadAll<List<int>>());
            }
            finally
            {
                newCacheHelper?.Dispose();
            }
        }
    }
}
