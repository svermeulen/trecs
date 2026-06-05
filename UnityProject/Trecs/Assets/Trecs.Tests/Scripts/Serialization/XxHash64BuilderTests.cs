using System;
using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Verifies the streaming <see cref="XxHash64Builder"/> produces a result byte-identical
    /// to the one-shot <see cref="CollisionResistantHashCalculator.ComputeXxHash64(ReadOnlySpan{byte})"/>
    /// over the concatenation of all updates, for every length and chunk split. This identity
    /// is load-bearing: the snapshot retain path hashes its two disjoint sections via the
    /// builder, and that value must equal the contiguous-path checksum recomputed at
    /// verify-time — otherwise desync detection false-positives on every frame.
    /// </summary>
    [TestFixture]
    public class XxHash64BuilderTests
    {
        static byte[] MakePattern(int length)
        {
            var bytes = new byte[length];
            for (int i = 0; i < length; i++)
            {
                bytes[i] = (byte)((i * 31 + 7) & 0xFF);
            }
            return bytes;
        }

        static ulong Streamed(byte[] data, int chunk)
        {
            var builder = XxHash64Builder.Create();
            for (int offset = 0; offset < data.Length; offset += chunk)
            {
                int len = Math.Min(chunk, data.Length - offset);
                builder.Update(new ReadOnlySpan<byte>(data, offset, len));
            }
            return builder.Digest();
        }

        [Test]
        public void Streaming_MatchesOneShot_AcrossLengthsAndChunkSizes(
            [Values(0, 1, 4, 7, 8, 9, 16, 31, 32, 33, 63, 64, 65, 127, 256, 1000)] int length
        )
        {
            var data = MakePattern(length);
            ulong oneShot = CollisionResistantHashCalculator.ComputeXxHash64(data, data.Length);

            // Block boundaries (32) and the sub-block tail handlers (8/4/1) are the risky
            // spots, so exercise chunk sizes that straddle them in every combination.
            foreach (var chunk in new[] { 1, 2, 3, 4, 5, 7, 8, 16, 31, 32, 33, 64, length + 1 })
            {
                if (chunk <= 0)
                    continue;
                ulong streamed = Streamed(data, chunk);
                NAssert.That(
                    streamed,
                    Is.EqualTo(oneShot),
                    "length={0} chunk={1}: streamed hash must equal one-shot",
                    length,
                    chunk
                );
            }
        }

        [Test]
        public void Streaming_EmptyUpdates_AreNoOps()
        {
            var data = MakePattern(50);
            ulong oneShot = CollisionResistantHashCalculator.ComputeXxHash64(data, data.Length);

            var builder = XxHash64Builder.Create();
            builder.Update(ReadOnlySpan<byte>.Empty);
            builder.Update(new ReadOnlySpan<byte>(data, 0, 20));
            builder.Update(ReadOnlySpan<byte>.Empty);
            builder.Update(new ReadOnlySpan<byte>(data, 20, 30));
            builder.Update(ReadOnlySpan<byte>.Empty);

            NAssert.That(builder.Digest(), Is.EqualTo(oneShot));
        }

        [Test]
        public void Streaming_MultiSegment_MatchesConcatenatedOneShot()
        {
            // Mirrors the real use: several disjoint segments hashed in order must equal a
            // one-shot hash over their concatenation.
            var a = MakePattern(16);
            var b = MakePattern(1000);
            var c = MakePattern(1);

            var concat = new byte[a.Length + b.Length + c.Length];
            Buffer.BlockCopy(a, 0, concat, 0, a.Length);
            Buffer.BlockCopy(b, 0, concat, a.Length, b.Length);
            Buffer.BlockCopy(c, 0, concat, a.Length + b.Length, c.Length);
            ulong oneShot = CollisionResistantHashCalculator.ComputeXxHash64(concat, concat.Length);

            var builder = XxHash64Builder.Create();
            builder.Update(a);
            builder.Update(b);
            builder.Update(c);

            NAssert.That(builder.Digest(), Is.EqualTo(oneShot));
        }
    }
}
