using System.Linq;
using Xunit;

namespace Trecs.SourceGen.Tests;

public class DuplicateTypeIdGeneratorTests
{
    // Shared prelude: declares a minimal [TypeId(int)] attribute in the Trecs
    // namespace so the generator's ForAttributeWithMetadataName lookup finds
    // it inside the snippet's own compilation. The Trecs assembly isn't
    // referenced from this test project; we simulate the attribute's shape.
    const string TypeIdAttributeStub =
        """
        namespace Trecs
        {
            [System.AttributeUsage(System.AttributeTargets.Struct
                | System.AttributeTargets.Class
                | System.AttributeTargets.Enum)]
            public class TypeIdAttribute : System.Attribute
            {
                public int Id { get; }
                public TypeIdAttribute(int id) { Id = id; }
            }
        }
        """;

    // Also reproduce enough of Trecs's namespace so the assembly-filter
    // helper accepts this as a Trecs-referencing compilation. The filter
    // checks the compilation's assembly name == "Trecs" OR that it
    // references an assembly named "Trecs". We can satisfy the first
    // branch by naming the test compilation "Trecs".
    const string Assembly = "Trecs";

    [Fact]
    public void NoTypeIds_NoDiagnostics()
    {
        var source = TypeIdAttributeStub + """

            namespace App
            {
                public struct Foo {}
                public struct Bar {}
            }
            """;

        var diagnostics = GeneratorTestHarness.RunGeneratorAndFilterToTrecs(
            new DuplicateTypeIdGenerator(),
            source
        );

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void UniqueTypeIds_NoDiagnostics()
    {
        var source = TypeIdAttributeStub + """

            namespace App
            {
                [Trecs.TypeId(100)] public struct Foo {}
                [Trecs.TypeId(200)] public struct Bar {}
                [Trecs.TypeId(300)] public struct Baz {}
            }
            """;

        var diagnostics = GeneratorTestHarness.RunGeneratorAndFilterToTrecs(
            new DuplicateTypeIdGenerator(),
            source
        );

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void TwoTypesSameId_ReportsDuplicateOnBoth()
    {
        var source = TypeIdAttributeStub + """

            namespace App
            {
                [Trecs.TypeId(42)] public struct Foo {}
                [Trecs.TypeId(42)] public struct Bar {}
            }
            """;

        var diagnostics = GeneratorTestHarness.RunGeneratorAndFilterToTrecs(
            new DuplicateTypeIdGenerator(),
            source
        );

        var duplicates = diagnostics.Where(d => d.Id == "TRECS120").ToList();
        Assert.Equal(2, duplicates.Count);

        // Each diagnostic should mention the colliding id (42) in its message.
        Assert.All(duplicates, d => Assert.Contains("42", d.GetMessage()));
    }

    [Fact]
    public void ThreeTypesSameId_ReportsOnAll()
    {
        var source = TypeIdAttributeStub + """

            namespace App
            {
                [Trecs.TypeId(7)] public struct A {}
                [Trecs.TypeId(7)] public struct B {}
                [Trecs.TypeId(7)] public struct C {}
            }
            """;

        var diagnostics = GeneratorTestHarness.RunGeneratorAndFilterToTrecs(
            new DuplicateTypeIdGenerator(),
            source
        );

        Assert.Equal(3, diagnostics.Count(d => d.Id == "TRECS120"));
    }

    [Fact]
    public void EnumTypeWithDuplicateId_AlsoFlagged()
    {
        // Generator registers on Struct | Class | Enum because [TypeId] is
        // valid on enums too. Verify the enum path participates in collision
        // detection.
        var source = TypeIdAttributeStub + """

            namespace App
            {
                [Trecs.TypeId(99)] public struct Thing {}
                [Trecs.TypeId(99)] public enum Color { Red, Blue }
            }
            """;

        var diagnostics = GeneratorTestHarness.RunGeneratorAndFilterToTrecs(
            new DuplicateTypeIdGenerator(),
            source
        );

        Assert.Equal(2, diagnostics.Count(d => d.Id == "TRECS120"));
    }

    [Fact]
    public void DisjointCollisionGroups_ReportedIndependently()
    {
        // Two separate collision groups: (1,1) and (2,2). Four diagnostics expected.
        var source = TypeIdAttributeStub + """

            namespace App
            {
                [Trecs.TypeId(1)] public struct A {}
                [Trecs.TypeId(1)] public struct B {}
                [Trecs.TypeId(2)] public struct C {}
                [Trecs.TypeId(2)] public struct D {}
                [Trecs.TypeId(3)] public struct E {}
            }
            """;

        var diagnostics = GeneratorTestHarness.RunGeneratorAndFilterToTrecs(
            new DuplicateTypeIdGenerator(),
            source
        );

        Assert.Equal(4, diagnostics.Count(d => d.Id == "TRECS120"));
    }
}
