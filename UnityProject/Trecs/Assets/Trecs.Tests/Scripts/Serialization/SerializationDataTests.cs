using System;
using System.Buffers;
using System.Buffers.Binary;
using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Standalone coverage for <see cref="SerializationData"/> and
    /// <see cref="SerializationDataPool"/> — the two-buffer retain representation. These tests
    /// don't go through the writer/reader (that's covered separately); they pin the contiguous
    /// wire-form layout and the no-materialization checksum against directly-populated buffers.
    /// </summary>
    [TestFixture]
    public class SerializationDataTests
    {
        static SerializationData MakeData(
            int version,
            long flags,
            bool includeTypeChecks,
            byte[] bitFieldBytes,
            int bitFieldBitCount,
            byte[] dataBytes
        )
        {
            var data = new SerializationData
            {
                Version = version,
                Flags = flags,
                IncludeTypeChecks = includeTypeChecks,
                BitFieldBitCount = bitFieldBitCount,
            };
            data.BitFieldsWriter.Write(bitFieldBytes);
            data.DataWriter.Write(dataBytes);
            return data;
        }

        static byte[] Pattern(int length, int salt)
        {
            var bytes = new byte[length];
            for (int i = 0; i < length; i++)
            {
                bytes[i] = (byte)((i * 17 + salt) & 0xFF);
            }
            return bytes;
        }

        [Test]
        public void ContiguousSize_AccountsForHeaderPrefixAndSections()
        {
            var data = MakeData(
                3,
                0xABL,
                true,
                Pattern(5, 1),
                bitFieldBitCount: 40,
                Pattern(100, 2)
            );

            // 16 header + 12 section prefix + 5 bit-field bytes + 100 data bytes.
            NAssert.That(data.ContiguousSize, Is.EqualTo(16 + 12 + 5 + 100));
        }

        [Test]
        public void CopyContiguousTo_ProducesHeaderPrefixThenSections()
        {
            var bitFields = Pattern(5, 1);
            var dataBytes = Pattern(40, 2);
            var data = MakeData(7, 0x1234L, includeTypeChecks: true, bitFields, 40, dataBytes);

            var dest = new byte[data.ContiguousSize];
            data.CopyContiguousTo(dest);

            // Header round-trips through the same util the reader uses.
            int offset = 0;
            var (version, flags, includeTypeChecks) = SerializationHeaderUtil.ReadHeader(
                dest,
                ref offset
            );
            NAssert.That(version, Is.EqualTo(7));
            NAssert.That(flags, Is.EqualTo(0x1234L));
            NAssert.That(includeTypeChecks, Is.True);

            // Section prefix: [bitCount][bitFieldByteCount][dataByteCount].
            int totalBits = BinaryPrimitives.ReadInt32LittleEndian(
                new ReadOnlySpan<byte>(dest, offset, 4)
            );
            int byteCount = BinaryPrimitives.ReadInt32LittleEndian(
                new ReadOnlySpan<byte>(dest, offset + 4, 4)
            );
            int dataByteCount = BinaryPrimitives.ReadInt32LittleEndian(
                new ReadOnlySpan<byte>(dest, offset + 8, 4)
            );
            NAssert.That(totalBits, Is.EqualTo(40));
            NAssert.That(byteCount, Is.EqualTo(bitFields.Length));
            NAssert.That(dataByteCount, Is.EqualTo(dataBytes.Length));
            offset += 12;

            NAssert.That(
                new ReadOnlySpan<byte>(dest, offset, bitFields.Length).ToArray(),
                Is.EqualTo(bitFields)
            );
            offset += bitFields.Length;
            NAssert.That(
                new ReadOnlySpan<byte>(dest, offset, dataBytes.Length).ToArray(),
                Is.EqualTo(dataBytes)
            );
            offset += dataBytes.Length;

            // No trailing sentinel: the payload ends exactly at the data section.
            NAssert.That(offset, Is.EqualTo(data.ContiguousSize));
        }

        [Test]
        public void WriteContiguousTo_MatchesCopyContiguousTo()
        {
            var data = MakeData(2, 0L, false, Pattern(9, 3), 70, Pattern(513, 4));

            var copyDest = new byte[data.ContiguousSize];
            data.CopyContiguousTo(copyDest);

            var writer = new ArrayBufferWriter<byte>();
            data.WriteContiguousTo(writer);

            NAssert.That(writer.WrittenSpan.ToArray(), Is.EqualTo(copyDest));
        }

        [Test]
        public void ComputeContiguousChecksum_EqualsOneShotHashOfContiguousBytes()
        {
            // The no-materialization streaming checksum must equal hashing the materialized
            // contiguous form — this is what keeps retain-path checksums identical to the
            // contiguous save/verify paths.
            var data = MakeData(5, 0xDEADL, true, Pattern(7, 5), 56, Pattern(2000, 6));

            var contiguous = new byte[data.ContiguousSize];
            data.CopyContiguousTo(contiguous);
            ulong expected = CollisionResistantHashCalculator.ComputeXxHash64(
                contiguous,
                contiguous.Length
            );

            NAssert.That(data.ComputeContiguousChecksum(), Is.EqualTo(expected));
        }

        [Test]
        public void ComputeContiguousChecksum_HandlesEmptySections()
        {
            var data = new SerializationData
            {
                Version = 1,
                Flags = 0,
                IncludeTypeChecks = false,
            };
            var contiguous = new byte[data.ContiguousSize];
            data.CopyContiguousTo(contiguous);
            ulong expected = CollisionResistantHashCalculator.ComputeXxHash64(
                contiguous,
                contiguous.Length
            );

            NAssert.That(data.ComputeContiguousChecksum(), Is.EqualTo(expected));
        }

        [Test]
        public void Clear_ResetsContentsButKeepsInstanceReusable()
        {
            var data = MakeData(9, 1L, true, Pattern(4, 7), 30, Pattern(10, 8));
            data.Clear();

            NAssert.That(data.Version, Is.EqualTo(0));
            NAssert.That(data.Flags, Is.EqualTo(0L));
            NAssert.That(data.IncludeTypeChecks, Is.False);
            NAssert.That(data.BitFieldBitCount, Is.EqualTo(0));
            NAssert.That(data.BitFieldBytes.Length, Is.EqualTo(0));
            NAssert.That(data.Data.Length, Is.EqualTo(0));
            NAssert.That(data.ContiguousSize, Is.EqualTo(16 + 12 + 0 + 0));
        }
    }

    [TestFixture]
    public class SerializationDataPoolTests
    {
        [Test]
        public void Spawn_ReturnsClearedInstance()
        {
            var pool = new SerializationDataPool();
            var data = pool.Spawn();
            NAssert.That(data.BitFieldBytes.Length, Is.EqualTo(0));
            NAssert.That(data.Data.Length, Is.EqualTo(0));
        }

        [Test]
        public void DespawnThenSpawn_ReusesSameInstance()
        {
            var pool = new SerializationDataPool();
            var a = pool.Spawn();
            pool.Despawn(a);
            var b = pool.Spawn();
            NAssert.That(b, Is.SameAs(a), "a despawned instance should be handed back out");
        }

        [Test]
        public void Despawn_DropsInstancesBeyondCap()
        {
            var pool = new SerializationDataPool(maxBuffers: 1);
            var a = pool.Spawn();
            var b = pool.Spawn();
            NAssert.That(b, Is.Not.SameAs(a));

            pool.Despawn(a); // pool now at cap (1)
            pool.Despawn(b); // dropped — over cap

            NAssert.That(pool.Spawn(), Is.SameAs(a), "first reuse returns the retained instance");
            NAssert.That(pool.Spawn(), Is.Not.SameAs(b), "over-cap instance was not retained");
        }

        [Test]
        public void Despawn_Null_IsNoOp()
        {
            var pool = new SerializationDataPool();
            NAssert.DoesNotThrow(() => pool.Despawn(null));
        }

#if DEBUG
        [Test]
        public void Despawn_Twice_Asserts()
        {
            var pool = new SerializationDataPool();
            var a = pool.Spawn();
            pool.Despawn(a);
            NAssert.Throws<TrecsException>(() => pool.Despawn(a));
        }
#endif
    }
}
