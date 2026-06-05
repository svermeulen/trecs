using System;
using System.Collections.Generic;
using NUnit.Framework;
using Trecs.Internal;
using UnityEngine;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Payload-level integrity: a well-formed payload round-trips, and truncated / empty / interior-
    /// damaged payloads are rejected. The contiguous wire form carries explicit section lengths (and
    /// no end-of-payload sentinel), so truncation is caught by the section-length bounds check at
    /// wrap time and an under/over-read by the full-consumption check at completion.
    /// </summary>
    [TestFixture]
    public class PayloadIntegrityTests
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
        public void ValidPayload_CompleteSerialization_RoundTrips()
        {
            var testData = new Vector3(1.0f, 2.0f, 3.0f);
            var flags = 0L;

            _helper.WriteAll(
                _data,
                testData,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var result = _helper.ReadAll<Vector3>(_data);

            NAssert.That(Mathf.Approximately(result.x, testData.x));
            NAssert.That(Mathf.Approximately(result.y, testData.y));
            NAssert.That(Mathf.Approximately(result.z, testData.z));
        }

        [Test]
        public void TruncatedTail_ThrowsException()
        {
            var testData = 42;
            var flags = 0L;

            _helper.WriteAll(
                _data,
                testData,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var serializedData = _data.ToContiguousBytes();

            // Drop the last 4 bytes: the section length prefix still claims the full data section,
            // so the bounds check at wrap time sees it run past the (shortened) buffer.
            var truncatedData = new byte[serializedData.Length - 4];
            Array.Copy(serializedData, truncatedData, truncatedData.Length);

            NAssert.Catch<Exception>(() => _helper.ReadAll<int>(_readBuffer.Wrap(truncatedData)));
        }

        [Test]
        public void TrailingBytesAfterPayload_AreIgnored()
        {
            var testData = 123;
            var flags = 0L;

            _helper.WriteAll(
                _data,
                testData,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var serializedData = _data.ToContiguousBytes();

            // Append garbage after the payload. The explicit data-section length lets the reader
            // slice exactly the payload region and ignore everything after it, even when the whole
            // (oversized) buffer is loaded.
            var extendedData = new byte[serializedData.Length + 4];
            Array.Copy(serializedData, extendedData, serializedData.Length);
            extendedData[serializedData.Length] = 0xFF;
            extendedData[serializedData.Length + 1] = 0xAA;
            extendedData[serializedData.Length + 2] = 0xBB;
            extendedData[serializedData.Length + 3] = 0xCC;

            var result = _helper.ReadAll<int>(_readBuffer.Wrap(extendedData));
            NAssert.That(result == testData);
        }

        [Test]
        public void InteriorTruncation_MissingElements_ThrowsException()
        {
            var testList = new List<int> { 1, 2, 3, 4, 5 };
            var flags = 0L;

            _helper.WriteAll(
                _data,
                testList,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var serializedData = _data.ToContiguousBytes();

            // Drop 8 bytes from the buffer. The section length prefix still claims the full data
            // section, so wrapping the shortened buffer fails the bounds check.
            var corruptedData = new byte[serializedData.Length - 8];
            Array.Copy(serializedData, 0, corruptedData, 0, corruptedData.Length);

            NAssert.Catch<Exception>(() =>
                _helper.ReadAll<List<int>>(_readBuffer.Wrap(corruptedData))
            );
        }

        [Test]
        public void EmptyStream_NoData_ThrowsException()
        {
            var emptyData = new byte[0];

            NAssert.Catch<Exception>(() => _helper.ReadAll<int>(_readBuffer.Wrap(emptyData)));
        }
    }
}
