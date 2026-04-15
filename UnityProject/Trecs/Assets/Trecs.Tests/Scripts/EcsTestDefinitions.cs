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
        public static readonly Tag StateA = new(990000005, "TestStateA");
        public static readonly Tag StateB = new(990000006, "TestStateB");
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

    public static class TestTemplates
    {
        public static Template SimpleAlpha =>
            new Template(
                debugName: "TestSimpleAlpha",
                localBaseTemplates: Array.Empty<Template>(),
                states: Array.Empty<TagSet>(),
                localComponentDeclarations: new IComponentDeclaration[]
                {
                    new ComponentDeclaration<TestInt>(
                        null,
                        null,
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
                states: Array.Empty<TagSet>(),
                localComponentDeclarations: new IComponentDeclaration[]
                {
                    new ComponentDeclaration<TestInt>(
                        null,
                        null,
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
                        null,
                        null,
                        default(TestFloat)
                    ),
                },
                localTags: new Tag[] { TestTags.Beta }
            );

        public static Template WithStates =>
            new Template(
                debugName: "TestWithStates",
                localBaseTemplates: Array.Empty<Template>(),
                states: new TagSet[]
                {
                    TagSet.FromTags(TestTags.StateA),
                    TagSet.FromTags(TestTags.StateB),
                },
                localComponentDeclarations: new IComponentDeclaration[]
                {
                    new ComponentDeclaration<TestInt>(
                        null,
                        null,
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
                        null,
                        null,
                        default(TestVec)
                    ),
                },
                localTags: new Tag[] { TestTags.Gamma }
            );

        public static Template ZeroComponents =>
            new Template(
                debugName: "TestZeroComponents",
                localBaseTemplates: Array.Empty<Template>(),
                states: Array.Empty<TagSet>(),
                localComponentDeclarations: Array.Empty<IComponentDeclaration>(),
                localTags: new Tag[] { TestTags.Epsilon }
            );

        public static Template WithDefaults =>
            new Template(
                debugName: "TestWithDefaults",
                localBaseTemplates: Array.Empty<Template>(),
                states: Array.Empty<TagSet>(),
                localComponentDeclarations: new IComponentDeclaration[]
                {
                    new ComponentDeclaration<TestInt>(
                        null,
                        null,
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
                states: Array.Empty<TagSet>(),
                localComponentDeclarations: new IComponentDeclaration[]
                {
                    new ComponentDeclaration<TestFloat>(
                        null,
                        null,
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
                states: Array.Empty<TagSet>(),
                localComponentDeclarations: new IComponentDeclaration[]
                {
                    new ComponentDeclaration<TestVec>(
                        null,
                        null,
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
