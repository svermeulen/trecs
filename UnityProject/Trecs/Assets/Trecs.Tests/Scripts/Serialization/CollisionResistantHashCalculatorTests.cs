using System.Text;
using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Pins the wire-format-bearing properties of
    /// <see cref="CollisionResistantHashCalculator.ComputeXxHash64(string)"/>. The value is
    /// serialized into snapshots (system pause identity, custom world-state section names),
    /// so it must stay byte-identical across refactors of the overload's implementation:
    /// it is defined as canonical xxHash64 (seed 0) over the UTF-8 bytes of the input.
    /// </summary>
    [TestFixture]
    public class CollisionResistantHashCalculatorTests
    {
        // Canonical xxHash64 reference vectors (seed 0), cross-checked against the
        // upstream C implementation. These protect against accidental wire-format
        // changes — e.g. switching the string overload to hash UTF-16 chars, or
        // changing the seed.
        [TestCase("", 0xEF46DB3751D8E999ul)]
        [TestCase("SystemEnableState", 0xBF1807A3CA1C23F6ul)]
        public void StringOverload_MatchesCanonicalXxHash64(string text, ulong expected)
        {
            NAssert.That(
                CollisionResistantHashCalculator.ComputeXxHash64(text),
                Is.EqualTo(expected)
            );
        }

        [Test]
        public void StringOverload_EqualsHashOfUtf8Bytes(
            [Values(
                "",
                "a",
                "SomeSystemName",
                "non-ascii: ⚡ Δt → ∞",
                "exactly-32-bytes-long-string!!!!"
            )]
                string text
        )
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            NAssert.That(
                CollisionResistantHashCalculator.ComputeXxHash64(text),
                Is.EqualTo(CollisionResistantHashCalculator.ComputeXxHash64(bytes, bytes.Length))
            );
        }

        [Test]
        public void StringOverload_LongInput_GrowsScratchAndStillMatches()
        {
            // Longer than the initial scratch buffer, forcing the growth path.
            var text = new string('x', 5000) + "⚡tail";
            var bytes = Encoding.UTF8.GetBytes(text);
            NAssert.That(
                CollisionResistantHashCalculator.ComputeXxHash64(text),
                Is.EqualTo(CollisionResistantHashCalculator.ComputeXxHash64(bytes, bytes.Length))
            );
        }

        [Test]
        public void StringOverload_ShortAfterLong_IgnoresStaleScratchTail()
        {
            // After a long input has grown/filled the shared scratch buffer, a short
            // input must hash only its own bytes, not stale tail bytes from the
            // previous call.
            CollisionResistantHashCalculator.ComputeXxHash64(new string('z', 4096));
            NAssert.That(
                CollisionResistantHashCalculator.ComputeXxHash64(""),
                Is.EqualTo(0xEF46DB3751D8E999ul)
            );
            var bytes = Encoding.UTF8.GetBytes("short");
            NAssert.That(
                CollisionResistantHashCalculator.ComputeXxHash64("short"),
                Is.EqualTo(CollisionResistantHashCalculator.ComputeXxHash64(bytes, bytes.Length))
            );
        }
    }
}
