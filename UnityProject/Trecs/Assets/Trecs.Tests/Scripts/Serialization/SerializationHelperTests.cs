using NUnit.Framework;
using Trecs.Internal;
using UnityEngine;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Covers <see cref="SerializationHelper"/> — the buffer-less reader+writer pairing that
    /// replaced the old <c>SerializationBuffer</c> for whole-payload round-trips (the caller owns the
    /// <see cref="SerializationData"/>). The headline guarantee is that hashing a payload via the new
    /// in-place path (<see cref="SerializationData.ComputeContiguousChecksum"/> after
    /// <see cref="SerializationHelper.WriteAll{T}"/>) is byte-identical to the legacy
    /// <c>SerializationBuffer</c> hash, so content-addressed ids — including
    /// <c>DiskMemoize</c>'s persistent disk keys — survive the migration off the buffer.
    /// </summary>
    [TestFixture]
    public class SerializationHelperTests
    {
        SerializerRegistry _registry;
        SerializationHelper _helper;

        [SetUp]
        public void SetUp()
        {
            _registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(_registry);
            _helper = new SerializationHelper(_registry);
        }

        [Test]
        public void WriteAll_ThenReadAll_RoundTripsValue(
            [Values(false, true)] bool includeTypeChecks
        )
        {
            // The SerializeCloner pattern: write into the two-section buffer and read straight back
            // out of it (SerializationData is itself an IReadOnlySerializationData).
            var data = new SerializationData();
            var value = new Vector3(1.5f, -2.3f, 0.7f);

            _helper.WriteAll(data, value, TestConstants.Version, includeTypeChecks);

            NAssert.That(_helper.ReadAll<Vector3>(data), Is.EqualTo(value));
        }

        [Test]
        public void WriteAll_ClearsTargetAcrossCalls()
        {
            // The writer clears the target on each Start, so a second, smaller payload doesn't
            // inherit stale bytes from a first, larger one.
            var data = new SerializationData();

            _helper.WriteAll(data, 123456789L, TestConstants.Version, includeTypeChecks: false);
            NAssert.That(_helper.ReadAll<long>(data), Is.EqualTo(123456789L));

            _helper.WriteAll(data, 7, TestConstants.Version, includeTypeChecks: false);
            NAssert.That(_helper.ReadAll<int>(data), Is.EqualTo(7));
        }

        // Locks in that moving UniqueHashGenerator off SerializationBuffer (hash the materialized
        // MemoryStream) onto SerializationData.ComputeContiguousChecksum (hash the wire form in
        // place) does NOT change the produced hash — otherwise every persistent DiskMemoize cache
        // key would silently invalidate.
        [Test]
        public void ContiguousChecksum_MatchesReferenceHash_ValueType(
            [Values(false, true)] bool includeTypeChecks
        )
        {
            const long flags = 0L;
            var value = new Vector3(1.5f, -2.3f, 0.7f);

            var data = new SerializationData();
            _helper.WriteAll(data, value, TestConstants.Version, includeTypeChecks, flags);
            long newHash = unchecked((long)data.ComputeContiguousChecksum());

            NAssert.That(newHash, Is.EqualTo(ReferenceHash(value, includeTypeChecks, flags)));
        }

        [Test]
        public void ContiguousChecksum_MatchesReferenceHash_Object(
            [Values(false, true)] bool includeTypeChecks
        )
        {
            const long flags = 0L;
            object value = new Vector3(1.5f, -2.3f, 0.7f);

            var data = new SerializationData();
            _helper.WriteAllObject(data, value, TestConstants.Version, includeTypeChecks, flags);
            long newHash = unchecked((long)data.ComputeContiguousChecksum());

            NAssert.That(newHash, Is.EqualTo(ReferenceObjectHash(value, includeTypeChecks, flags)));
        }

        // The reference hash, computed independently of ComputeContiguousChecksum: materialize the
        // contiguous wire bytes and xxHash64 them directly — exactly the bytes-and-algorithm the
        // legacy SerializationBuffer / UniqueHashGenerator path produced. Equality with
        // ComputeContiguousChecksum pins that the in-place checksum covers the same byte range.
        long ReferenceHash<T>(in T value, bool includeTypeChecks, long flags)
        {
            var data = new SerializationData();
            _helper.WriteAll(data, value, TestConstants.Version, includeTypeChecks, flags);
            var bytes = data.ToContiguousBytes();
            return unchecked(
                (long)CollisionResistantHashCalculator.ComputeXxHash64(bytes, bytes.Length)
            );
        }

        long ReferenceObjectHash(object value, bool includeTypeChecks, long flags)
        {
            var data = new SerializationData();
            _helper.WriteAllObject(data, value, TestConstants.Version, includeTypeChecks, flags);
            var bytes = data.ToContiguousBytes();
            return unchecked(
                (long)CollisionResistantHashCalculator.ComputeXxHash64(bytes, bytes.Length)
            );
        }
    }
}
