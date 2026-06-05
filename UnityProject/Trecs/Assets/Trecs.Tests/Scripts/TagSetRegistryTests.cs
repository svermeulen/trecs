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
        static readonly Tag TagA = new(0x70000001);
        static readonly Tag TagB = new(0x70000002);
        static readonly Tag TagC = new(0x70000005);
        static readonly Tag TagD = new(0x70000006);

        // Cross-arity inputs: XGuid = aGuid XOR bGuid by construction.
        static readonly Tag CrossArityA = new(0x70000013 ^ 0x12345678);
        static readonly Tag CrossArityB = new(0x12345678);
        static readonly Tag CrossArityX = new(0x70000013);

        // Zero-XOR trio: ZeroA ^ ZeroB ^ ZeroC == 0, so the non-empty set {A,B,C} collides
        // with the empty/null set (id 0) and must be rejected at registration.
        static readonly Tag ZeroXorA = new(0x40000001);
        static readonly Tag ZeroXorB = new(0x20000002);
        static readonly Tag ZeroXorC = new(0x60000003); // == A ^ B, so A ^ B ^ C == 0

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
                TagA.Value ^ TagB.Value,
                TagC.Value ^ TagD.Value,
                "Test inputs no longer collide; pick new guids."
            );

            // First registration succeeds and claims the slot (or it was already
            // claimed by a prior test run — either way the second registration
            // with mismatched tags must throw).
            TagSet.FromTags(TagA, TagB);

            // Second registration with different tags must throw.
            var ex = NAssert.Throws<TrecsException>(() => TagSet.FromTags(TagC, TagD));
            StringAssert.Contains("XOR-hash collision", ex.Message);
        }

        [Test]
        public void TagsToTagSet_SingleTagWithColludingSetId_Throws()
        {
            // Cross-arity collision: a singleton set {X} has id = X.Value;
            // a two-element set {A, B} has id = A.Value ^ B.Value. If those
            // happen to be equal, both register at the same id slot.
            NAssert.AreEqual(CrossArityA.Value ^ CrossArityB.Value, CrossArityX.Value);

            TagSet.FromTags(CrossArityA, CrossArityB);
            var ex = NAssert.Throws<TrecsException>(() => TagSet.FromTags(CrossArityX));
            StringAssert.Contains("XOR-hash collision", ex.Message);
        }

        [Test]
        public void CombineWith_CollidingUnion_Throws()
        {
            // Combine() has its own (now allocation-free, always-on) collision check.
            // Register {A,B} first, then combine the singletons {C} and {D} whose union
            // XORs to the same id (A^B == C^D) with different members — must throw rather
            // than alias {A,B}.
            NAssert.AreEqual(TagA.Value ^ TagB.Value, TagC.Value ^ TagD.Value);

            TagSet.FromTags(TagA, TagB);

            var ex = NAssert.Throws<TrecsException>(() =>
                TagSet.FromTags(TagC).CombineWith(TagSet.FromTags(TagD))
            );
            StringAssert.Contains("XOR-hash collision", ex.Message);
        }

        [Test]
        public void NonEmptySetXoringToZero_ThrowsCollisionWithEmptySet()
        {
            // id 0 is reserved for the empty/null set, and ids are never remapped, so a
            // non-empty set whose members XOR to 0 collides with the empty set. It must be
            // rejected with a loud throw (not silently remapped to a different id), so that
            // id == XOR(members) holds exactly for every set that exists.

            // Self-check: the trio XORs to zero.
            NAssert.AreEqual(
                0,
                ZeroXorA.Value ^ ZeroXorB.Value ^ ZeroXorC.Value,
                "Test inputs no longer XOR to zero; pick new guids."
            );

            var ex = NAssert.Throws<TrecsException>(() =>
                TagSet.FromTags(ZeroXorA, ZeroXorB, ZeroXorC)
            );
            StringAssert.Contains("XOR to 0", ex.Message);
        }

        [Test]
        public void EmptySet_InternsAtZeroWithNoMembers()
        {
            // The empty/null set keeps id 0 and resolves to an empty member list — this is
            // the value the zero-XOR throw above protects.
            NAssert.AreEqual(0, TagSet.Null.Id);
            NAssert.AreEqual(0, TagSet.Null.Tags.Count);
        }

        [Test]
        public void XorDistinct_DedupsRepeatedIds_MatchingRegistrySemantics()
        {
            // The dedup-XOR underlying BurstableFromTags must XOR only the distinct ids,
            // so a duplicate type argument yields the same id the managed registry path
            // produces (the deduped set) instead of XOR-cancelling to a divergent value.
            NAssert.AreEqual(5, TagSet.XorDistinct(5, 5));
            NAssert.AreEqual(5 ^ 6, TagSet.XorDistinct(5, 6));

            NAssert.AreEqual(5 ^ 6, TagSet.XorDistinct(5, 6, 5)); // dup of first
            NAssert.AreEqual(5 ^ 6, TagSet.XorDistinct(5, 5, 6)); // dup of second
            NAssert.AreEqual(5, TagSet.XorDistinct(5, 5, 5));
            NAssert.AreEqual(5 ^ 6 ^ 7, TagSet.XorDistinct(5, 6, 7));

            NAssert.AreEqual(5 ^ 6 ^ 7, TagSet.XorDistinct(5, 6, 7, 5));
            NAssert.AreEqual(5 ^ 6 ^ 7, TagSet.XorDistinct(5, 6, 5, 7));
            NAssert.AreEqual(5 ^ 6, TagSet.XorDistinct(5, 6, 5, 6));
            NAssert.AreEqual(5, TagSet.XorDistinct(5, 5, 5, 5));
        }

        [Test]
        public void RemoveDimensionTags_StripsActiveVariantViaDirectXor()
        {
            // Now that id == XOR(members) exactly, the dim edits XOR directly against the
            // stored id. Removing TagA from {TagA, TagB} yields {TagB} == TagB.Value.
            var current = TagSet.FromTags(TagA, TagB);

            var result = WorldInfo.RemoveDimensionTags(current, TagA);
            NAssert.AreEqual(TagB.Value, result.Id);
        }

        [Test]
        public void ReplaceDimensionTags_SwapsActiveVariantViaDirectXor()
        {
            // Replacing TagA with TagC in {TagA, TagB} yields {TagB, TagC} == TagB ^ TagC.
            var current = TagSet.FromTags(TagA, TagB);

            var result = WorldInfo.ReplaceDimensionTags(current, TagA, TagC);
            NAssert.AreEqual(TagB.Value ^ TagC.Value, result.Id);
        }

        // ── BurstableFromTags equivalence ────────────────────────────────────
        //
        // The Burst-safe XOR-based factory must produce the same TagSet.Id as
        // the registry-routed managed TagSet<T...>.Value for every input. If it
        // diverges, every Burst-job AddEntity<T...> call lands in the wrong
        // group (or fails to resolve). These are the foundational correctness
        // tests for the Burst-side fast path.

        [Test]
        public void BurstableFromTags_OneArg_MatchesGenericTagSet()
        {
            NAssert.AreEqual(TagSet<TestAlpha>.Value, TagSet.BurstableFromTags<TestAlpha>());
        }

        [Test]
        public void BurstableFromTags_TwoArgs_MatchesGenericTagSet()
        {
            NAssert.AreEqual(
                TagSet<TestAlpha, TestBeta>.Value,
                TagSet.BurstableFromTags<TestAlpha, TestBeta>()
            );
        }

        [Test]
        public void BurstableFromTags_ThreeArgs_MatchesGenericTagSet()
        {
            NAssert.AreEqual(
                TagSet<TestAlpha, TestBeta, TestGamma>.Value,
                TagSet.BurstableFromTags<TestAlpha, TestBeta, TestGamma>()
            );
        }

        [Test]
        public void BurstableFromTags_FourArgs_MatchesGenericTagSet()
        {
            NAssert.AreEqual(
                TagSet<TestAlpha, TestBeta, TestGamma, TestDelta>.Value,
                TagSet.BurstableFromTags<TestAlpha, TestBeta, TestGamma, TestDelta>()
            );
        }

        [Test]
        public void CompareTo_ReturnsCorrectOrdering()
        {
            var tagSetA = TagSet<TestAlpha>.Value;
            var tagSetB = TagSet<TestBeta>.Value;

            if (tagSetA.Id < tagSetB.Id)
            {
                NAssert.Less(
                    tagSetA.CompareTo(tagSetB),
                    0,
                    "CompareTo should return negative when this.Id < other.Id"
                );
                NAssert.Greater(
                    tagSetB.CompareTo(tagSetA),
                    0,
                    "CompareTo should return positive when this.Id > other.Id"
                );
            }
            else
            {
                NAssert.Greater(
                    tagSetA.CompareTo(tagSetB),
                    0,
                    "CompareTo should return positive when this.Id > other.Id"
                );
                NAssert.Less(
                    tagSetB.CompareTo(tagSetA),
                    0,
                    "CompareTo should return negative when this.Id < other.Id"
                );
            }

            NAssert.AreEqual(
                0,
                tagSetA.CompareTo(tagSetA),
                "CompareTo should return 0 for equal TagSets"
            );
        }

#if DEBUG
        [Test]
        public void BurstableFromTags_DuplicateTypeArgs_AssertsInDebug()
        {
            // XOR cancels duplicates, so the resulting id would diverge from
            // the managed-side dedup path. The DEBUG-only assert catches the
            // misuse loudly.
            NAssert.Catch(() => TagSet.BurstableFromTags<TestAlpha, TestAlpha>());
            NAssert.Catch(() => TagSet.BurstableFromTags<TestAlpha, TestBeta, TestAlpha>());
            NAssert.Catch(() =>
                TagSet.BurstableFromTags<TestAlpha, TestBeta, TestGamma, TestAlpha>()
            );
        }
#endif
    }
}
