using NUnit.Framework;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    // Verifies the TagSetRegistry's hash-collision guard. TagSet identity is
    // the XOR of member tag GUIDs, which is non-injective: two distinct sets
    // can XOR to the same int. Without the guard, the second registration
    // silently returns a TagSet aliased to the first set's contents — a
    // particularly nasty bug because queries and group lookups would
    // confidently return wrong results. The guard converts the collision
    // into a loud TrecsException at the second registration.
    //
    // We construct adversarial tags via the public `Tag(int, string)` constructor,
    // which bypasses TagFactory.CreateTag (the hashed-ID path) but still
    // registers a debug name (required for safe Tag.ToString in the
    // collision-throw diagnostic). The GUIDs are chosen at the far end of
    // the int range so they're vanishingly unlikely to collide with FNV-1a
    // hashes of real test-tag type names.
    //
    // Tag instances are declared as static fields so the debug-name
    // registration happens exactly once per AppDomain — repeated test runs
    // pollute the global TagSetRegistry, but each Tag instance is
    // idempotent.
    [TestFixture]
    public class TagSetRegistryTests
    {
        // Pair (A=0x70000001, B=0x70000002): XOR = 0x70000003.
        // Pair (C=0x70000005, D=0x70000006): XOR = 0x70000003 also — same
        // last-two-bits trick that bit the migration's TestPartition guids.
        static readonly Tag TagA = new(0x70000001, "ColTestTagA");
        static readonly Tag TagB = new(0x70000002, "ColTestTagB");
        static readonly Tag TagC = new(0x70000005, "ColTestTagC");
        static readonly Tag TagD = new(0x70000006, "ColTestTagD");

        // Cross-arity inputs: XGuid = aGuid XOR bGuid by construction.
        static readonly Tag CrossArityA = new(0x70000013 ^ 0x12345678, "CrossArityA");
        static readonly Tag CrossArityB = new(0x12345678, "CrossArityB");
        static readonly Tag CrossArityX = new(0x70000013, "CrossArityX");

        [Test]
        public void TagsToTagSet_SameTagsTwice_ReturnsSameId()
        {
            // Sanity: cache-hit on the SAME tags must succeed. The guard's
            // happy path is exercised here — different ordering still hashes
            // to the same id and the existing entry matches.
            var first = TagSet.FromTags(TagA, TagB);
            var second = TagSet.FromTags(TagB, TagA); // reversed; XOR is commutative
            NAssert.AreEqual(first.Id, second.Id);
        }

        [Test]
        public void TagsToTagSet_ColludingPairs_Throws()
        {
            // Register A^B first, then attempt C^D (same XOR). The guard
            // must surface this as a TrecsException rather than silently
            // returning a TagSet aliased to {A,B}.

            // Self-check: confirm the adversarial pair actually collides on XOR.
            NAssert.AreEqual(
                TagA.Guid ^ TagB.Guid,
                TagC.Guid ^ TagD.Guid,
                "Test inputs no longer collide; pick new guids."
            );

            // First registration succeeds and claims the slot (or it was already
            // claimed by a prior test run — either way the second registration
            // with mismatched tags must throw).
            TagSet.FromTags(TagA, TagB);

            // Second registration with different tags must throw.
            var ex = NAssert.Throws<TrecsException>(() => TagSet.FromTags(TagC, TagD));
            StringAssert.Contains("TagSet hash collision", ex.Message);
        }

        [Test]
        public void TagsToTagSet_SingleTagWithColludingSetId_Throws()
        {
            // Cross-arity collision: a singleton set {X} has id = X.Guid;
            // a two-element set {A, B} has id = A.Guid ^ B.Guid. If those
            // happen to be equal, both register at the same id slot.
            NAssert.AreEqual(CrossArityA.Guid ^ CrossArityB.Guid, CrossArityX.Guid);

            TagSet.FromTags(CrossArityA, CrossArityB);
            var ex = NAssert.Throws<TrecsException>(() => TagSet.FromTags(CrossArityX));
            StringAssert.Contains("TagSet hash collision", ex.Message);
        }
    }
}
