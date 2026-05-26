using System;
using NUnit.Framework;
using Trecs.Internal;
using Trecs.Serialization;
using Assert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Round-trip coverage for the collision-resistant hash extension on
    /// <see cref="SerializationBuffer"/>. The extension is used by
    /// <c>UniqueHashGenerator</c> to derive stable IDs from serialized
    /// content; if it broke, blob/asset IDs would silently change.
    ///
    /// Also covers the sibling <see cref="SerializationBuffer.ComputeChecksum"/>
    /// method, which is used by <c>SnapshotSerializer.ComputeChecksum</c> for
    /// replay desync detection. The two share xxHash64 but call it through different
    /// surfaces, and both need to pin the "hash only the written portion of
    /// the stream, not the underlying buffer capacity" contract — the
    /// <c>MemoryStream.GetBuffer()</c> / <c>MemoryStream.Length</c> discipline
    /// is easy to get wrong.
    /// </summary>
    [TestFixture]
    public class SerializationBufferHashTests
    {
        SerializerRegistry _registry;
        SerializationBuffer _buffer;

        [SetUp]
        public void SetUp()
        {
            _registry = TestSerializerInstaller.CreateTestRegistry();
            _registry.RegisterSerializer(new BlitArraySerializer<byte>());
            _buffer = new SerializationBuffer(_registry);
        }

        [TearDown]
        public void TearDown()
        {
            _buffer?.Dispose();
        }

        [Test]
        public void GetMemoryStreamCollisionResistantHash_IsConsistent()
        {
            // Same bytes must produce the same hash on repeated calls (the
            // hash is what UniqueHashGenerator uses to derive stable IDs).
            var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
            _buffer.WriteAll(data, TestConstants.Version, includeTypeChecks: true);
            _buffer.ResetMemoryPosition();

            long first = _buffer.GetMemoryStreamCollisionResistantHash();
            long second = _buffer.GetMemoryStreamCollisionResistantHash();

            Assert.That(first, Is.Not.Zero, "hash should not be zero for non-empty content");
            Assert.That(second, Is.EqualTo(first), "same bytes must produce same hash");
        }

        [Test]
        public void GetMemoryStreamCollisionResistantHash_DiffersForDifferentContent()
        {
            var dataA = new byte[] { 1, 2, 3, 4, 5 };
            var dataB = new byte[] { 1, 2, 3, 4, 6 };

            _buffer.WriteAll(dataA, TestConstants.Version, includeTypeChecks: true);
            _buffer.ResetMemoryPosition();
            long hashA = _buffer.GetMemoryStreamCollisionResistantHash();

            _buffer.ClearMemoryStream();
            _buffer.WriteAll(dataB, TestConstants.Version, includeTypeChecks: true);
            _buffer.ResetMemoryPosition();
            long hashB = _buffer.GetMemoryStreamCollisionResistantHash();

            Assert.That(hashB, Is.Not.EqualTo(hashA));
        }

        [Test]
        public void ComputeChecksum_IsConsistent()
        {
            // Same bytes must produce the same checksum on repeated calls.
            // Determinism is load-bearing for replay verification — if the
            // checksum isn't stable per-input, desync detection breaks.
            var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
            _buffer.LoadMemoryStreamFromBytes(data);

            ulong first = _buffer.ComputeChecksum();
            ulong second = _buffer.ComputeChecksum();

            Assert.That(first, Is.Not.Zero, "checksum should not be zero for non-empty content");
            Assert.That(second, Is.EqualTo(first), "same bytes must produce same checksum");
        }

        [Test]
        public void ComputeChecksum_HashesOnlyWrittenBytes_NotFullCapacity()
        {
            // SerializationBuffer's internal MemoryStream is allocated with a
            // capacity larger than typical small payloads (1024 bytes on
            // construction). MemoryStream.GetBuffer() returns the full
            // underlying array regardless of Length, so a buggy implementation
            // that passed buffer.Length to xxHash64 (instead of
            // MemoryStream.Length) would hash uninitialized trailing bytes and
            // produce a different result.
            //
            // We verify the contract by computing the checksum two ways:
            //   (a) via SerializationBuffer.ComputeChecksum() on a small
            //       payload sitting inside a larger underlying buffer
            //   (b) directly via CollisionResistantHashCalculator on a freshly
            //       allocated byte[] of exactly the written length
            // These must match.
            var data = new byte[] { 42, 43, 44, 45, 46, 47, 48, 49, 50, 51 };
            _buffer.LoadMemoryStreamFromBytes(data);

            // Sanity: the underlying buffer must be larger than the written
            // length for this test to be meaningful. (If MemoryStream's
            // initial capacity ever changes such that this no longer holds for
            // small payloads, the test still passes — but it stops exercising
            // the regression it's guarding against, so fail loudly here.)
            Assert.That(
                _buffer.MemoryStream.GetBuffer().Length,
                Is.GreaterThan(data.Length),
                "test setup requires underlying buffer capacity > written length "
                    + "to exercise the GetBuffer/Length pitfall"
            );

            ulong actual = _buffer.ComputeChecksum();
            ulong expected = CollisionResistantHashCalculator.ComputeXxHash64(data, data.Length);

            Assert.That(
                actual,
                Is.EqualTo(expected),
                "ComputeChecksum must hash only the written portion of the "
                    + "stream, not the underlying buffer's full capacity"
            );
        }

        [Test]
        public void ComputeChecksum_EmptyStream_ReturnsXxHash64OfEmptyInput()
        {
            // Zero-length stream is well-defined: xxHash64 has a stable
            // empty-input value derived from the avalanche of seed +
            // PRIME64_5. We pin that behavior here so a future implementation
            // change that early-returns 0 (or throws) for empty input would be
            // caught.
            Assert.That(_buffer.MemoryLength, Is.EqualTo(0), "test prerequisite");

            ulong actual = _buffer.ComputeChecksum();
            ulong expected = CollisionResistantHashCalculator.ComputeXxHash64(
                Array.Empty<byte>(),
                0
            );

            Assert.That(actual, Is.EqualTo(expected));
        }

        [Test]
        public void ComputeChecksum_DiffersForDifferentContent()
        {
            // Sanity check: a single-bit flip changes the output. xxHash64 has
            // excellent avalanche properties, so this is essentially
            // guaranteed for distinct inputs, but the assertion catches a
            // regression where ComputeChecksum was stubbed out or always
            // returned the same value.
            var dataA = new byte[] { 1, 2, 3, 4, 5 };
            var dataB = new byte[] { 1, 2, 3, 4, 6 };

            _buffer.LoadMemoryStreamFromBytes(dataA);
            ulong hashA = _buffer.ComputeChecksum();

            _buffer.ClearMemoryStream();
            _buffer.LoadMemoryStreamFromBytes(dataB);
            ulong hashB = _buffer.ComputeChecksum();

            Assert.That(hashB, Is.Not.EqualTo(hashA));
        }
    }
}
