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
    public partial class QTestEntityA : ITemplate, ITagged<QId1>, ITagged<QCatA>
    {
        TestInt TestInt;
        TestFloat TestFloat;
    }

    // Has QCatA + QCatB + two components
    public partial class QTestEntityAB : ITemplate, ITagged<QId2>, ITagged<QCatA>, ITagged<QCatB>
    {
        TestInt TestInt;
        TestFloat TestFloat;
    }

    // Has QCatB + only TestInt (no TestFloat, for MatchByComponents tests)
    public partial class QTestEntityB : ITemplate, ITagged<QId3>, ITagged<QCatB>
    {
        TestInt TestInt;
    }

    // Has QCatA + all five test components (for multi-interface aspect tests)
    public partial class QTestEntityAll : ITemplate, ITagged<QId4>, ITagged<QCatA>
    {
        TestInt TestInt;
        TestFloat TestFloat;
        TestVec TestVec;
        TestBool TestBool;
        TestShort TestShort;
    }

    // --- Shared Set ---
    public struct QTestSetA : IEntitySet<QId1> { }

    // --- Shared Aspects ---

    partial struct QSingleTagView : IAspect, IRead<TestInt> { }
}
