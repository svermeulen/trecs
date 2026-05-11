using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    // Tags used by the resolver-tiebreaker test fixtures. Kept distinct from the
    // shared test tags so registration ordering and tag-set hashes don't leak
    // across fixtures.
    public struct ResolverShape : ITag { }

    public struct ResolverBall : ITag { }

    public struct ResolverActive : ITag { }

    public struct ResolverInactive : ITag { }

    public struct ResolverPlayer : ITag { }

    public struct ResolverEnemy : ITag { }

    public struct ResolverCharacter : ITag { }

    public struct ResolverFlying : ITag { }

    // Base used only via IExtends — never registered concretely.
    public partial class ResolverShapeBase : ITemplate, ITagged<ResolverShape>
    {
        TestInt TestInt;
    }

    // Inheritance + binary presence/absence partition.
    // Groups: {ResolverShape, ResolverBall} and
    //         {ResolverShape, ResolverBall, ResolverActive}.
    public partial class ResolverBallEntity
        : ITemplate,
            ITagged<ResolverBall>,
            IExtends<ResolverShapeBase>,
            IPartitionedBy<ResolverActive> { }

    // Single template, single arity-2 multi-variant dim.
    // Groups: {ResolverBall, ResolverActive} and {ResolverBall, ResolverInactive}.
    public partial class ResolverBallVariantEntity
        : ITemplate,
            ITagged<ResolverBall>,
            IPartitionedBy<ResolverActive, ResolverInactive>
    {
        TestInt TestInt;
    }

    // Cross-template overlap (no inheritance). Both templates tag with
    // ResolverCharacter; a query on just ResolverCharacter matches both.
    public partial class ResolverPlayerEntity
        : ITemplate,
            ITagged<ResolverPlayer, ResolverCharacter>
    {
        TestInt TestInt;
    }

    public partial class ResolverEnemyEntity : ITemplate, ITagged<ResolverEnemy, ResolverCharacter>
    {
        TestInt TestInt;
    }

    // Inheritance + binary partition + an additional unrelated presence/absence
    // dim. Groups: {Shape, Ball}, {Shape, Ball, Active}, {Shape, Ball, Flying},
    // {Shape, Ball, Active, Flying}. A query of <Ball> would match all four —
    // {Shape, Ball} is the unique subset minimum.
    public partial class ResolverBallTwoDimEntity
        : ITemplate,
            ITagged<ResolverBall>,
            IExtends<ResolverShapeBase>,
            IPartitionedBy<ResolverActive>,
            IPartitionedBy<ResolverFlying> { }

    // Single template, only the base tag — used for cross-template tests
    // where one template's tag set is a strict subset of another's. Registered
    // standalone (no IExtends relationship to ResolverPlayerEntity etc.).
    public partial class ResolverNakedCharacterEntity : ITemplate, ITagged<ResolverCharacter>
    {
        TestInt TestInt;
    }

    [TestFixture]
    public class GetSingleGroupWithTagsTests
    {
        [Test]
        public void InheritanceAndPartition_QueryByOwnTag_ResolvesToBaseGroup()
        {
            // Two groups: {Shape, Ball} (absent) and {Shape, Ball, Active} (present).
            // Both contain Ball as a subset. Same template, unique smallest →
            // resolves to the absent partition.
            using var env = EcsTestHelper.CreateEnvironment(b => { }, ResolverBallEntity.Template);
            var wi = env.Accessor.WorldInfo;

            var group = wi.GetSingleGroupWithTags(TagSet<ResolverBall>.Value);
            var tags = wi.GetGroupTags(group);

            NAssert.IsTrue(tags.Contains(Tag<ResolverShape>.Value));
            NAssert.IsTrue(tags.Contains(Tag<ResolverBall>.Value));
            NAssert.IsFalse(tags.Contains(Tag<ResolverActive>.Value));
        }

        [Test]
        public void InheritanceAndPartition_QueryWithPresentTag_ResolvesToPresentGroup()
        {
            using var env = EcsTestHelper.CreateEnvironment(b => { }, ResolverBallEntity.Template);
            var wi = env.Accessor.WorldInfo;

            // Only {Shape, Ball, Active} contains Active, so the query already
            // narrows to one group — no tiebreaker needed.
            var group = wi.GetSingleGroupWithTags(TagSet<ResolverBall, ResolverActive>.Value);
            var tags = wi.GetGroupTags(group);

            NAssert.IsTrue(tags.Contains(Tag<ResolverActive>.Value));
        }

        [Test]
        public void InheritanceAndPartition_QueryByBaseTag_ResolvesToAbsentGroup()
        {
            using var env = EcsTestHelper.CreateEnvironment(b => { }, ResolverBallEntity.Template);
            var wi = env.Accessor.WorldInfo;

            // Shape alone matches both Ball groups; same template, unique
            // smallest → {Shape, Ball}.
            var group = wi.GetSingleGroupWithTags(TagSet<ResolverShape>.Value);
            var tags = wi.GetGroupTags(group);

            NAssert.IsTrue(tags.Contains(Tag<ResolverBall>.Value));
            NAssert.IsFalse(tags.Contains(Tag<ResolverActive>.Value));
        }

        [Test]
        public void InheritanceAndPartition_AddEntityByOwnTag_LandsInAbsentGroup()
        {
            using var env = EcsTestHelper.CreateEnvironment(b => { }, ResolverBallEntity.Template);
            var a = env.Accessor;

            a.AddEntity<ResolverBall>().Set(new TestInt { Value = 1 }).AssertComplete();
            a.SubmitEntities();

            NAssert.AreEqual(
                1,
                a.Query()
                    .WithTags<ResolverShape, ResolverBall>()
                    .WithoutTags<ResolverActive>()
                    .Count()
            );
            NAssert.AreEqual(
                0,
                a.Query().WithTags<ResolverShape, ResolverBall, ResolverActive>().Count()
            );
        }

        [Test]
        public void MultiVariantPartition_QueryByBaseTag_ThrowsAmbiguous()
        {
            // Groups: {Ball, Active} and {Ball, Inactive}. A query of <Ball>
            // matches both, same size — siblings, no unique minimum → throws.
            using var env = EcsTestHelper.CreateEnvironment(
                b => { },
                ResolverBallVariantEntity.Template
            );
            var wi = env.Accessor.WorldInfo;

            NAssert.Throws<TrecsException>(() =>
            {
                wi.GetSingleGroupWithTags(TagSet<ResolverBall>.Value);
            });
        }

        [Test]
        public void MultiVariantPartition_QueryWithVariant_Resolves()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                b => { },
                ResolverBallVariantEntity.Template
            );
            var wi = env.Accessor.WorldInfo;

            // <Ball, Active> uniquely identifies the Active group.
            var group = wi.GetSingleGroupWithTags(TagSet<ResolverBall, ResolverActive>.Value);
            var tags = wi.GetGroupTags(group);

            NAssert.IsTrue(tags.Contains(Tag<ResolverActive>.Value));
            NAssert.IsFalse(tags.Contains(Tag<ResolverInactive>.Value));
        }

        [Test]
        public void CrossTemplate_SharedTag_ThrowsAmbiguous()
        {
            // Two unrelated templates both contain ResolverCharacter — the
            // tiebreaker refuses to silently pick across template boundaries.
            using var env = EcsTestHelper.CreateEnvironment(
                b => { },
                ResolverPlayerEntity.Template,
                ResolverEnemyEntity.Template
            );
            var wi = env.Accessor.WorldInfo;

            NAssert.Throws<TrecsException>(() =>
            {
                wi.GetSingleGroupWithTags(TagSet<ResolverCharacter>.Value);
            });
        }

        [Test]
        public void CrossTemplate_DiscriminatorTag_Resolves()
        {
            // ResolverPlayer is unique to ResolverPlayerEntity — no ambiguity.
            using var env = EcsTestHelper.CreateEnvironment(
                b => { },
                ResolverPlayerEntity.Template,
                ResolverEnemyEntity.Template
            );
            var wi = env.Accessor.WorldInfo;

            var group = wi.GetSingleGroupWithTags(TagSet<ResolverPlayer>.Value);
            var tags = wi.GetGroupTags(group);

            NAssert.IsTrue(tags.Contains(Tag<ResolverPlayer>.Value));
            NAssert.IsFalse(tags.Contains(Tag<ResolverEnemy>.Value));
        }

        [Test]
        public void InheritanceAndTwoPartitionDims_QueryByOwnTag_ResolvesToBaseGroup()
        {
            // Four groups under one template. <Ball> matches all four;
            // {Shape, Ball} (count 2) is the unique smallest and is a subset
            // of every other match — resolves cleanly.
            using var env = EcsTestHelper.CreateEnvironment(
                b => { },
                ResolverBallTwoDimEntity.Template
            );
            var wi = env.Accessor.WorldInfo;

            var group = wi.GetSingleGroupWithTags(TagSet<ResolverBall>.Value);
            var tags = wi.GetGroupTags(group);

            NAssert.IsTrue(tags.Contains(Tag<ResolverShape>.Value));
            NAssert.IsTrue(tags.Contains(Tag<ResolverBall>.Value));
            NAssert.IsFalse(tags.Contains(Tag<ResolverActive>.Value));
            NAssert.IsFalse(tags.Contains(Tag<ResolverFlying>.Value));
        }

        [Test]
        public void InheritanceAndTwoPartitionDims_QueryWithOneDim_ResolvesToNarrowestChain()
        {
            // <Ball, Active> matches {Shape, Ball, Active} (size 3) and
            // {Shape, Ball, Active, Flying} (size 4). The smaller is a subset
            // of the larger — clean chain → resolves to {Shape, Ball, Active}.
            using var env = EcsTestHelper.CreateEnvironment(
                b => { },
                ResolverBallTwoDimEntity.Template
            );
            var wi = env.Accessor.WorldInfo;

            var group = wi.GetSingleGroupWithTags(TagSet<ResolverBall, ResolverActive>.Value);
            var tags = wi.GetGroupTags(group);

            NAssert.IsTrue(tags.Contains(Tag<ResolverActive>.Value));
            NAssert.IsFalse(tags.Contains(Tag<ResolverFlying>.Value));
        }

        // McTestEntity (defined in PresenceAbsenceTests.cs) gives us a single
        // template with one presence/absence dim (McPoisoned) and one
        // multi-variant dim (McAlive / McDead). Groups:
        //   {McBase, McAlive}
        //   {McBase, McAlive, McPoisoned}
        //   {McBase, McDead}
        //   {McBase, McDead, McPoisoned}

        [Test]
        public void MixedDims_QueryByBaseTag_ThrowsSiblingsAtSmallest()
        {
            // <McBase> matches all four groups. Smallest size is 2, but two
            // groups tie at that size ({McBase, McAlive} and {McBase, McDead})
            // — siblings on the variant dim → throws.
            using var env = EcsTestHelper.CreateEnvironment(b => { }, McTestEntity.Template);
            var wi = env.Accessor.WorldInfo;

            NAssert.Throws<TrecsException>(() =>
            {
                wi.GetSingleGroupWithTags(TagSet<McBase>.Value);
            });
        }

        [Test]
        public void MixedDims_QueryWithVariantTag_ResolvesToBarePartition()
        {
            // <McBase, McAlive> matches {McBase, McAlive} (size 2) and
            // {McBase, McAlive, McPoisoned} (size 3). The smaller is a subset
            // of the larger — clean chain → resolves to {McBase, McAlive}.
            using var env = EcsTestHelper.CreateEnvironment(b => { }, McTestEntity.Template);
            var wi = env.Accessor.WorldInfo;

            var group = wi.GetSingleGroupWithTags(TagSet<McBase, McAlive>.Value);
            var tags = wi.GetGroupTags(group);

            NAssert.IsTrue(tags.Contains(Tag<McAlive>.Value));
            NAssert.IsFalse(tags.Contains(Tag<McPoisoned>.Value));
            NAssert.IsFalse(tags.Contains(Tag<McDead>.Value));
        }

        [Test]
        public void MixedDims_QueryWithPresenceTag_ThrowsSiblingsAcrossVariant()
        {
            // <McBase, McPoisoned> matches {McBase, McAlive, McPoisoned} and
            // {McBase, McDead, McPoisoned} — both size 3, siblings on the
            // variant dim → throws.
            using var env = EcsTestHelper.CreateEnvironment(b => { }, McTestEntity.Template);
            var wi = env.Accessor.WorldInfo;

            NAssert.Throws<TrecsException>(() =>
            {
                wi.GetSingleGroupWithTags(TagSet<McBase, McPoisoned>.Value);
            });
        }

        [Test]
        public void MixedDims_FullySpecified_Resolves()
        {
            // <McBase, McAlive, McPoisoned> matches exactly one group — early
            // single-match return.
            using var env = EcsTestHelper.CreateEnvironment(b => { }, McTestEntity.Template);
            var wi = env.Accessor.WorldInfo;

            var group = wi.GetSingleGroupWithTags(TagSet<McBase, McAlive, McPoisoned>.Value);
            var tags = wi.GetGroupTags(group);

            NAssert.IsTrue(tags.Contains(Tag<McAlive>.Value));
            NAssert.IsTrue(tags.Contains(Tag<McPoisoned>.Value));
            NAssert.IsFalse(tags.Contains(Tag<McDead>.Value));
        }

        [Test]
        public void CrossTemplate_SubsetSizesDiffer_StillThrows()
        {
            // ResolverNakedCharacterEntity registers tag set {Character}
            // (size 1); ResolverPlayerEntity registers {Player, Character}
            // (size 2). A query of <Character> matches both, with the
            // ResolverNakedCharacterEntity group being a strict subset of
            // ResolverPlayerEntity's. The resolver must reject this on the
            // cross-template check, not silently pick the size-1 group.
            using var env = EcsTestHelper.CreateEnvironment(
                b => { },
                ResolverNakedCharacterEntity.Template,
                ResolverPlayerEntity.Template
            );
            var wi = env.Accessor.WorldInfo;

            NAssert.Throws<TrecsException>(() =>
            {
                wi.GetSingleGroupWithTags(TagSet<ResolverCharacter>.Value);
            });
        }

        [Test]
        public void AddEntity_CrossTemplate_Throws()
        {
            // Resolver errors surface through AddEntity (the most common
            // entry point) — not just direct WorldInfo queries.
            using var env = EcsTestHelper.CreateEnvironment(
                b => { },
                ResolverPlayerEntity.Template,
                ResolverEnemyEntity.Template
            );
            var a = env.Accessor;

            NAssert.Throws<TrecsException>(() =>
            {
                a.AddEntity<ResolverCharacter>().Set(new TestInt { Value = 1 }).AssertComplete();
            });
        }

        [Test]
        public void Warmup_WithSubsetQuery_WarmsAllMatchingGroups()
        {
            // Warmup uses plural resolution — Warmup<Ball> warms BOTH
            // {Shape, Ball} (absent) and {Shape, Ball, Active} (present), not
            // just the resolver's subset-minimum pick. Verified by adding
            // entities into both partitions after the single Warmup call.
            using var env = EcsTestHelper.CreateEnvironment(b => { }, ResolverBallEntity.Template);
            var a = env.Accessor;

            a.Warmup<ResolverBall>(initialCapacity: 16);

            a.AddEntity<ResolverBall>().Set(new TestInt { Value = 1 }).AssertComplete();
            a.AddEntity<ResolverBall, ResolverActive>()
                .Set(new TestInt { Value = 2 })
                .AssertComplete();
            a.SubmitEntities();

            NAssert.AreEqual(
                1,
                a.Query()
                    .WithTags<ResolverShape, ResolverBall>()
                    .WithoutTags<ResolverActive>()
                    .Count()
            );
            NAssert.AreEqual(
                1,
                a.Query().WithTags<ResolverShape, ResolverBall, ResolverActive>().Count()
            );
        }

        [Test]
        public void AmbiguousError_CrossTemplate_ListsMatchingGroups()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                b => { },
                ResolverPlayerEntity.Template,
                ResolverEnemyEntity.Template
            );
            var wi = env.Accessor.WorldInfo;

            var ex = NAssert.Throws<TrecsException>(() =>
            {
                wi.GetSingleGroupWithTags(TagSet<ResolverCharacter>.Value);
            });

            // The error must name both templates so the user can see what
            // collided rather than just "ambiguous".
            NAssert.That(ex.Message, Does.Contain("ResolverPlayerEntity"));
            NAssert.That(ex.Message, Does.Contain("ResolverEnemyEntity"));
            NAssert.That(ex.Message, Does.Contain("multiple templates"));
        }

        [Test]
        public void AmbiguousError_MultiVariantSiblings_ListsMatchingGroups()
        {
            using var env = EcsTestHelper.CreateEnvironment(b => { }, McTestEntity.Template);
            var wi = env.Accessor.WorldInfo;

            // <McBase, McPoisoned> matches both {McBase, McAlive, McPoisoned}
            // and {McBase, McDead, McPoisoned}.
            var ex = NAssert.Throws<TrecsException>(() =>
            {
                wi.GetSingleGroupWithTags(TagSet<McBase, McPoisoned>.Value);
            });

            // The error must surface the variant tags that distinguish the
            // tied matches so the user knows which dim to disambiguate on.
            NAssert.That(ex.Message, Does.Contain("McTestEntity"));
            NAssert.That(ex.Message, Does.Contain("McAlive"));
            NAssert.That(ex.Message, Does.Contain("McDead"));
            NAssert.That(ex.Message, Does.Contain("smallest tag-set size"));
        }

        [Test]
        public void Warmup_CrossTemplate_WarmsAllMatchingGroups()
        {
            // Warmup uses plural resolution, so cross-template tag queries
            // that AddEntity would reject as ambiguous are valid here —
            // Warmup<Character> warms every group containing Character
            // across both templates.
            using var env = EcsTestHelper.CreateEnvironment(
                b => { },
                ResolverPlayerEntity.Template,
                ResolverEnemyEntity.Template
            );
            var a = env.Accessor;

            a.Warmup<ResolverCharacter>(initialCapacity: 4);

            a.AddEntity<ResolverPlayer>().Set(new TestInt { Value = 1 }).AssertComplete();
            a.AddEntity<ResolverEnemy>().Set(new TestInt { Value = 2 }).AssertComplete();
            a.SubmitEntities();

            NAssert.AreEqual(1, a.Query().WithTags<ResolverPlayer>().Count());
            NAssert.AreEqual(1, a.Query().WithTags<ResolverEnemy>().Count());
        }

        [Test]
        public void Warmup_NoMatchingGroups_Throws()
        {
            // Warmup still throws if zero groups match — degenerate case is
            // surfaced as an error rather than a silent no-op.
            using var env = EcsTestHelper.CreateEnvironment(b => { }, ResolverBallEntity.Template);
            var a = env.Accessor;

            // ResolverEnemy is registered as a Tag<> elsewhere in this fixture
            // but no group in this env contains it.
            NAssert.Throws<TrecsException>(() =>
            {
                a.Warmup<ResolverEnemy>(initialCapacity: 4);
            });
        }

        [Test]
        public void WorldBuild_RegisteringBaseAndDerived_Throws()
        {
            // ResolverBallEntity declares IExtends<ResolverShapeBase>. If both
            // are passed to the WorldBuilder, the build-time assert in
            // WorldInfo catches the configuration up-front rather than letting
            // every AddEntity / Warmup / [FromWorld] call site fail later.
            NAssert.Throws<TrecsException>(() =>
            {
                using var env = EcsTestHelper.CreateEnvironment(
                    b => { },
                    ResolverShapeBase.Template,
                    ResolverBallEntity.Template
                );
            });
        }
    }
}
