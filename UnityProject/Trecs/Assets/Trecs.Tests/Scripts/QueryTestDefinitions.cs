namespace Trecs.Tests
{
    // Unique-per-template tags (for unambiguous entity creation)
    public struct QId1 : ITag { }

    public struct QId2 : ITag { }

    public struct QId3 : ITag { }

    public struct QId4 : ITag { }

    // Categorization tags (for query filtering)
    public struct QCatA : ITag { }

    public struct QCatB : ITag { }

    // --- Templates ---

    // Has QCatA + two components
    public partial class QTestEntityA : ITemplate, IHasTags<QId1>, IHasTags<QCatA>
    {
        public TestInt TestInt;
        public TestFloat TestFloat;
    }

    // Has QCatA + QCatB + two components
    public partial class QTestEntityAB : ITemplate, IHasTags<QId2>, IHasTags<QCatA>, IHasTags<QCatB>
    {
        public TestInt TestInt;
        public TestFloat TestFloat;
    }

    // Has QCatB + only TestInt (no TestFloat, for MatchByComponents tests)
    public partial class QTestEntityB : ITemplate, IHasTags<QId3>, IHasTags<QCatB>
    {
        public TestInt TestInt;
    }

    // Has QCatA + all five test components (for multi-interface aspect tests)
    public partial class QTestEntityAll : ITemplate, IHasTags<QId4>, IHasTags<QCatA>
    {
        public TestInt TestInt;
        public TestFloat TestFloat;
        public TestVec TestVec;
        public TestBool TestBool;
        public TestShort TestShort;
    }

    // --- Shared Set ---
    public struct QTestSetA : IEntitySet<QId1> { }

    // --- Shared Aspects ---

    partial struct QSingleTagView : IAspect, IRead<TestInt> { }
}
