using System;
using Trecs.Internal;

namespace Trecs.Tests
{
    // All test tags are ITag structs; their integer IDs come from
    // TagFactory's hash of the type's FullName, which has good distribution
    // and participates in the framework's per-type collision detection.
    // (Sequentially-numbered hardcoded IDs were how this used to be done;
    // they bypassed TagFactory and produced predictable XOR collisions on
    // consecutive pairs — see git history if curious.)
    public struct TestAlpha : ITag { }

    public struct TestBeta : ITag { }

    public struct TestGamma : ITag { }

    public struct TestDelta : ITag { }

    public struct TestEpsilon : ITag { }

    public struct TestPartitionA : ITag { }

    public struct TestPartitionB : ITag { }

    public static class TestTags
    {
        public static readonly Tag Alpha = Tag<TestAlpha>.Value;
        public static readonly Tag Beta = Tag<TestBeta>.Value;
        public static readonly Tag Gamma = Tag<TestGamma>.Value;
        public static readonly Tag Delta = Tag<TestDelta>.Value;
        public static readonly Tag PartitionA = Tag<TestPartitionA>.Value;
        public static readonly Tag PartitionB = Tag<TestPartitionB>.Value;
        public static readonly Tag Epsilon = Tag<TestEpsilon>.Value;
    }

    public partial struct TestInt : IEntityComponent
    {
        public int Value;
    }

    public partial struct TestFloat : IEntityComponent
    {
        public float Value;
    }

    public partial struct TestVec : IEntityComponent
    {
        public float X;
        public float Y;
    }

    public partial struct TestBool : IEntityComponent
    {
        public bool Value;
    }

    public partial struct TestShort : IEntityComponent
    {
        public short Value;
    }

    public static class TestTemplates
    {
        public static Template SimpleAlpha =>
            new Template(
                debugName: "TestSimpleAlpha",
                localBaseTemplates: Array.Empty<Template>(),
                partitions: Array.Empty<TagSet>(),
                localComponentDeclarations: new IComponentDeclaration[]
                {
                    new ComponentDeclaration<TestInt>(
                        null,
                        null,
                        null,
                        null,
                        null,
                        default(TestInt)
                    ),
                },
                localTags: new Tag[] { TestTags.Alpha }
            );

        public static Template TwoCompBeta =>
            new Template(
                debugName: "TestTwoCompBeta",
                localBaseTemplates: Array.Empty<Template>(),
                partitions: Array.Empty<TagSet>(),
                localComponentDeclarations: new IComponentDeclaration[]
                {
                    new ComponentDeclaration<TestInt>(
                        null,
                        null,
                        null,
                        null,
                        null,
                        default(TestInt)
                    ),
                    new ComponentDeclaration<TestFloat>(
                        null,
                        null,
                        null,
                        null,
                        null,
                        default(TestFloat)
                    ),
                },
                localTags: new Tag[] { TestTags.Beta }
            );

        public static Template WithPartitions =>
            new Template(
                debugName: "TestWithPartitions",
                localBaseTemplates: Array.Empty<Template>(),
                partitions: new TagSet[]
                {
                    TagSet.FromTags(TestTags.PartitionA),
                    TagSet.FromTags(TestTags.PartitionB),
                },
                localComponentDeclarations: new IComponentDeclaration[]
                {
                    new ComponentDeclaration<TestInt>(
                        null,
                        null,
                        null,
                        null,
                        null,
                        default(TestInt)
                    ),
                    new ComponentDeclaration<TestVec>(
                        null,
                        null,
                        null,
                        null,
                        null,
                        default(TestVec)
                    ),
                },
                localTags: new Tag[] { TestTags.Gamma },
                dimensions: new TagSet[]
                {
                    TagSet.FromTags(TestTags.PartitionA, TestTags.PartitionB),
                }
            );

        public static Template WithDefaults =>
            new Template(
                debugName: "TestWithDefaults",
                localBaseTemplates: Array.Empty<Template>(),
                partitions: Array.Empty<TagSet>(),
                localComponentDeclarations: new IComponentDeclaration[]
                {
                    new ComponentDeclaration<TestInt>(
                        null,
                        null,
                        null,
                        null,
                        null,
                        new TestInt { Value = 42 }
                    ),
                    new ComponentDeclaration<TestFloat>(
                        null,
                        null,
                        null,
                        null,
                        null,
                        new TestFloat { Value = 3.14f }
                    ),
                },
                localTags: new Tag[] { TestTags.Delta }
            );

        public static Template ChildOfAlpha =>
            new Template(
                debugName: "TestChildOfAlpha",
                localBaseTemplates: new Template[] { SimpleAlpha },
                partitions: Array.Empty<TagSet>(),
                localComponentDeclarations: new IComponentDeclaration[]
                {
                    new ComponentDeclaration<TestFloat>(
                        null,
                        null,
                        null,
                        null,
                        null,
                        default(TestFloat)
                    ),
                },
                localTags: new Tag[] { TestTags.Beta }
            );

        public static Template ChildWithDefaults =>
            new Template(
                debugName: "TestChildWithDefaults",
                localBaseTemplates: new Template[] { WithDefaults },
                partitions: Array.Empty<TagSet>(),
                localComponentDeclarations: new IComponentDeclaration[]
                {
                    new ComponentDeclaration<TestVec>(
                        null,
                        null,
                        null,
                        null,
                        null,
                        default(TestVec)
                    ),
                },
                localTags: new Tag[] { TestTags.Epsilon }
            );
    }
}
