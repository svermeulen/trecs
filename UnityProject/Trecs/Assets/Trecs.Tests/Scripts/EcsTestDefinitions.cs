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

    // A pair used by WorldSchemaFingerprintTests to prove the source-gen layout
    // hash distinguishes a same-size field reorder. Both are 8 bytes
    // (UnsafeUtility.SizeOf identical), so only the generated
    // __TrecsComponentLayoutHash tells them apart.
    public partial struct TestLayoutIntFloat : IEntityComponent
    {
        public int A;
        public float B;
    }

    public partial struct TestLayoutFloatInt : IEntityComponent
    {
        public float B;
        public int A;
    }

    public static class TestTemplates
    {
        public static Template SimpleAlpha =>
            TestTemplate
                .Named("TestSimpleAlpha")
                .WithTags(TestTags.Alpha)
                .WithComponent<TestInt>(default(TestInt));

        public static Template TwoCompBeta =>
            TestTemplate
                .Named("TestTwoCompBeta")
                .WithTags(TestTags.Beta)
                .WithComponent<TestInt>(default(TestInt))
                .WithComponent<TestFloat>(default(TestFloat));

        public static Template WithPartitions =>
            TestTemplate
                .Named("TestWithPartitions")
                .WithTags(TestTags.Gamma)
                .WithPartitions(
                    TagSet.FromTags(TestTags.PartitionA),
                    TagSet.FromTags(TestTags.PartitionB)
                )
                .WithDimensions(TagSet.FromTags(TestTags.PartitionA, TestTags.PartitionB))
                .WithComponent<TestInt>(default(TestInt))
                .WithComponent<TestVec>(default(TestVec));

        public static Template WithDefaults =>
            TestTemplate
                .Named("TestWithDefaults")
                .WithTags(TestTags.Delta)
                .WithComponent<TestInt>(new TestInt { Value = 42 })
                .WithComponent<TestFloat>(new TestFloat { Value = 3.14f });

        public static Template ChildOfAlpha =>
            TestTemplate
                .Named("TestChildOfAlpha")
                .Extending(SimpleAlpha)
                .WithTags(TestTags.Beta)
                .WithComponent<TestFloat>(default(TestFloat));

        public static Template ChildWithDefaults =>
            TestTemplate
                .Named("TestChildWithDefaults")
                .Extending(WithDefaults)
                .WithTags(TestTags.Epsilon)
                .WithComponent<TestVec>(default(TestVec));
    }
}
