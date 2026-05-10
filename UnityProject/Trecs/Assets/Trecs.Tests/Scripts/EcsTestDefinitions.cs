using System;
using Trecs.Internal;

namespace Trecs.Tests
{
    public static class TestTags
    {
        public static readonly Tag Alpha = new(990000001, "TestAlpha");
        public static readonly Tag Beta = new(990000002, "TestBeta");
        public static readonly Tag Gamma = new(990000003, "TestGamma");
        public static readonly Tag Delta = new(990000004, "TestDelta");
        public static readonly Tag PartitionA = new(990000005, "TestPartitionA");
        public static readonly Tag PartitionB = new(990000006, "TestPartitionB");
        public static readonly Tag Epsilon = new(990000007, "TestEpsilon");
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
                localTags: new Tag[] { TestTags.Gamma }
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
