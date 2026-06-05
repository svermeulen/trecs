using System;
using NUnit.Framework;
using Trecs.Internal;
using UnityEngine;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Covers the writer/reader SerializationData paths:
    /// <see cref="BinarySerializationWriter.Start(SerializationData, int, bool, long, bool)"/>
    /// + <see cref="BinarySerializationWriter.Complete"/> and
    /// <see cref="BinarySerializationReader.Start(IReadOnlySerializationData)"/>. The headline
    /// guarantee is that the streaming <see cref="SerializationData.ComputeContiguousChecksum"/>
    /// equals hashing the materialized contiguous bytes, and that the contiguous form round-trips
    /// back through the existing <c>ReadOnlyMemory</c> reader, so on-disk format and checksums
    /// are stable.
    /// </summary>
    [TestFixture]
    public class SerializationDataWriterReaderTests
    {
        SerializerRegistry _registry;
        BinarySerializationWriter _writer;
        BinarySerializationReader _reader;

        [SetUp]
        public void SetUp()
        {
            _registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(_registry);
            _writer = new BinarySerializationWriter(_registry);
            _reader = new BinarySerializationReader(_registry);
        }

        // A payload that exercises both sections: data writes (int/float/long/string) and
        // bit writes (explicit WriteBit, plus the null bits emitted by WriteString), enough
        // bits to spill several bit-field bytes.
        static void WriteSample(BinarySerializationWriter w)
        {
            w.Write("i", 42);
            w.WriteBit(true);
            w.WriteString("s", "hello world");
            w.WriteBit(false);
            w.Write("f", 3.14f);
            w.WriteString("nullStr", null);
            w.Write("l", 123456789L);
            w.Write("v", new Vector3(1.5f, -2.3f, 0.7f));
            for (int k = 0; k < 20; k++)
            {
                w.WriteBit(k % 3 == 0);
            }
        }

        static void ReadAndAssertSample(BinarySerializationReader r)
        {
            int i = default;
            r.Read("i", ref i);
            NAssert.That(i, Is.EqualTo(42));
            NAssert.That(r.ReadBit(), Is.True);
            NAssert.That(r.ReadString("s"), Is.EqualTo("hello world"));
            NAssert.That(r.ReadBit(), Is.False);
            float f = default;
            r.Read("f", ref f);
            NAssert.That(f, Is.EqualTo(3.14f));
            NAssert.That(r.ReadString("nullStr"), Is.Null);
            long l = default;
            r.Read("l", ref l);
            NAssert.That(l, Is.EqualTo(123456789L));
            Vector3 v = default;
            r.Read("v", ref v);
            NAssert.That(v, Is.EqualTo(new Vector3(1.5f, -2.3f, 0.7f)));
            for (int k = 0; k < 20; k++)
            {
                NAssert.That(r.ReadBit(), Is.EqualTo(k % 3 == 0), "bit {0}", k);
            }
        }

        [Test]
        public void RoundTrip_ThroughSerializationData([Values(false, true)] bool includeTypeChecks)
        {
            var data = new SerializationData();
            _writer.Start(data, TestConstants.Version, includeTypeChecks);
            WriteSample(_writer);
            _writer.Complete();

            NAssert.That(data.Version, Is.EqualTo(TestConstants.Version));
            NAssert.That(data.IncludeTypeChecks, Is.EqualTo(includeTypeChecks));

            _reader.Start(data);
            ReadAndAssertSample(_reader);
            _reader.Complete();
        }

        [Test]
        public void ContiguousChecksum_MatchesHashOfMaterializedBytes(
            [Values(false, true)] bool includeTypeChecks
        )
        {
            const long flags = 0L;

            var data = new SerializationData();
            _writer.Start(data, TestConstants.Version, includeTypeChecks, flags);
            WriteSample(_writer);
            _writer.Complete();

            var bytes = new byte[data.ContiguousSize];
            data.CopyContiguousTo(bytes);

            // The no-materialization streaming checksum must equal hashing the contiguous bytes,
            // so capture-time and verify-time checksums agree.
            NAssert.That(
                data.ComputeContiguousChecksum(),
                Is.EqualTo(CollisionResistantHashCalculator.ComputeXxHash64(bytes, bytes.Length))
            );
        }

        [Test]
        public void ContiguousFormFromSerializationData_ReadsBackViaContiguousReader()
        {
            // A SerializationData materialized to contiguous bytes must be loadable by wrapping it
            // in a ContiguousSerializationData view (this is how embedded bundle snapshots survive).
            var data = new SerializationData();
            _writer.Start(data, TestConstants.Version, includeTypeChecks: true);
            WriteSample(_writer);
            _writer.Complete();

            var bytes = new byte[data.ContiguousSize];
            data.CopyContiguousTo(bytes);

            var view = new ContiguousSerializationData(new ReadOnlyMemory<byte>(bytes));
            _reader.Start(view);
            ReadAndAssertSample(_reader);
            _reader.Complete();
        }
    }
}
