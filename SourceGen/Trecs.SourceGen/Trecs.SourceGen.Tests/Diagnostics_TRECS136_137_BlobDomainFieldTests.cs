using System.Linq;
using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Negative tests for the blob determinism-domain field rules (TRECS136-137), both emitted from
/// TemplateValidator. TRECS136 is the converse of TRECS121: an input pointer must not escape into
/// a persistent component (frame-scoped handle; retention beyond the delivering frame is
/// history-locker-dependent and snapshotting one stores a bare id replay cannot honor). TRECS137
/// bans anchors in any component: an anchor's PtrHandle is a live BlobCache handle that does not
/// survive serialization. See trecs docs/maintainers/maintainer-docs/blob-determinism-domains.md.
/// </summary>
[TestFixture]
public class Diagnostics_TRECS136_137_BlobDomainFieldTests
{
    [Test]
    public void TRECS136_InputSharedPtr_InPersistentComponent_Fires()
    {
        const string source = """
            namespace Sample
            {
                public class MyBlob { }

                public partial struct CState : Trecs.IEntityComponent
                {
                    public Trecs.InputSharedPtr<MyBlob> Blob;
                }

                public partial class PlayerTemplate : Trecs.ITemplate
                {
                    CState State;
                }
            }
            """;

        AssertDiagnostic(source, "TRECS136");
    }

    [Test]
    public void TRECS136_InputNativeSharedPtr_InPersistentComponent_Fires()
    {
        const string source = """
            namespace Sample
            {
                public partial struct CState : Trecs.IEntityComponent
                {
                    public Trecs.InputNativeSharedPtr<int> Blob;
                }

                public partial class PlayerTemplate : Trecs.ITemplate
                {
                    CState State;
                }
            }
            """;

        AssertDiagnostic(source, "TRECS136");
    }

    [Test]
    public void TRECS136_InputPtr_InInputComponent_DoesNotFire()
    {
        // The supported home for an input pointer: a Reset-mode [Input] component.
        const string source = """
            namespace Sample
            {
                public class MyBlob { }

                public partial struct CCmd : Trecs.IEntityComponent
                {
                    public Trecs.InputSharedPtr<MyBlob> Blob;
                }

                public partial class PlayerTemplate : Trecs.ITemplate
                {
                    [Trecs.Input(Trecs.MissingInputBehavior.Reset)]
                    CCmd Command;
                }
            }
            """;

        AssertNoDiagnostic(source, "TRECS136");
    }

    [Test]
    public void TRECS136_SharedPtr_InPersistentComponent_DoesNotFire()
    {
        // Positive control: the migration target (convert the in-hand input pointer, store the
        // sim-owned SharedPtr) must not itself trip TRECS136.
        const string source = """
            namespace Sample
            {
                public class MyBlob { }

                public partial struct CState : Trecs.IEntityComponent
                {
                    public Trecs.SharedPtr<MyBlob> Blob;
                }

                public partial class PlayerTemplate : Trecs.ITemplate
                {
                    CState State;
                }
            }
            """;

        AssertNoDiagnostic(source, "TRECS136");
    }

    [Test]
    public void TRECS137_SharedAnchor_InPersistentComponent_Fires()
    {
        const string source = """
            namespace Sample
            {
                public class MyBlob { }

                public partial struct CState : Trecs.IEntityComponent
                {
                    public Trecs.SharedAnchor<MyBlob> Pin;
                }

                public partial class PlayerTemplate : Trecs.ITemplate
                {
                    CState State;
                }
            }
            """;

        AssertDiagnostic(source, "TRECS137");
    }

    [Test]
    public void TRECS137_NativeSharedAnchor_InInputComponent_Fires()
    {
        // Anchors are banned in input components too — the input stream serializes component
        // bytes, and the embedded cache handle is equally stale on replay.
        const string source = """
            namespace Sample
            {
                public partial struct CCmd : Trecs.IEntityComponent
                {
                    public Trecs.NativeSharedAnchor<int> Pin;
                }

                public partial class PlayerTemplate : Trecs.ITemplate
                {
                    [Trecs.Input(Trecs.MissingInputBehavior.Reset)]
                    CCmd Command;
                }
            }
            """;

        AssertDiagnostic(source, "TRECS137");
    }

    [Test]
    public void TRECS137_SharedPtr_DoesNotFire()
    {
        // Positive control: the snapshotted refcounted handle is the supported component shape.
        const string source = """
            namespace Sample
            {
                public partial struct CState : Trecs.IEntityComponent
                {
                    public Trecs.NativeSharedPtr<int> Blob;
                }

                public partial class PlayerTemplate : Trecs.ITemplate
                {
                    CState State;
                }
            }
            """;

        AssertNoDiagnostic(source, "TRECS137");
    }

    [Test]
    public void TRECS136_InputPtr_NestedInPlainSubStruct_Fires()
    {
        // The escape the recursive walk closes: a forbidden input pointer buried inside a
        // plain sub-struct (not itself a component) used to slip past the one-level walk.
        const string source = """
            namespace Sample
            {
                public class MyBlob { }

                public struct Inner
                {
                    public Trecs.InputSharedPtr<MyBlob> Ptr;
                }

                public partial struct CState : Trecs.IEntityComponent
                {
                    public Inner Nested;
                }

                public partial class PlayerTemplate : Trecs.ITemplate
                {
                    CState State;
                }
            }
            """;

        AssertDiagnostic(source, "TRECS136");
        AssertDiagnosticMessageContains(source, "TRECS136", "Nested.Ptr");
    }

    [Test]
    public void TRECS137_Anchor_NestedTwoLevelsDeep_Fires()
    {
        // Two levels of nesting: the walk recurses through both plain sub-structs and the
        // diagnostic names the full dotted path so the offending field is findable.
        const string source = """
            namespace Sample
            {
                public class MyBlob { }

                public struct Deep
                {
                    public Trecs.SharedAnchor<MyBlob> Pin;
                }

                public struct Mid
                {
                    public Deep D;
                }

                public partial struct CState : Trecs.IEntityComponent
                {
                    public Mid M;
                }

                public partial class PlayerTemplate : Trecs.ITemplate
                {
                    CState State;
                }
            }
            """;

        AssertDiagnostic(source, "TRECS137");
        AssertDiagnosticMessageContains(source, "TRECS137", "M.D.Pin");
    }

    [Test]
    public void NestedManagedField_InPlainSubStruct_Fires()
    {
        // The managed-field rule (TRECS036) also walks recursively now: a reference-type
        // field buried in a sub-struct is rejected with its full path.
        const string source = """
            namespace Sample
            {
                public struct Inner
                {
                    public string Name;
                }

                public partial struct CState : Trecs.IEntityComponent
                {
                    public Inner Nested;
                }

                public partial class PlayerTemplate : Trecs.ITemplate
                {
                    CState State;
                }
            }
            """;

        AssertDiagnostic(source, "TRECS036");
        AssertDiagnosticMessageContains(source, "TRECS036", "Nested.Name");
    }

    static void AssertDiagnostic(string source, string expectedId)
    {
        var run = GeneratorTestHarness.Run(
            new IIncrementalGenerator[]
            {
                new TemplateDefinitionGenerator(),
                new EntityComponentGenerator(),
                new VariableUpdateOnlyValidator(),
            },
            source
        );
        var diag = run.GenDiagnostics.FirstOrDefault(d => d.Id == expectedId);
        Assert.That(diag, Is.Not.Null, $"Expected {expectedId}, got:\n{run.Format()}");
    }

    static void AssertDiagnosticMessageContains(string source, string id, string expectedSubstring)
    {
        var run = GeneratorTestHarness.Run(
            new IIncrementalGenerator[]
            {
                new TemplateDefinitionGenerator(),
                new EntityComponentGenerator(),
                new VariableUpdateOnlyValidator(),
            },
            source
        );
        var diag = run.GenDiagnostics.FirstOrDefault(d => d.Id == id);
        Assert.That(diag, Is.Not.Null, $"Expected {id}, got:\n{run.Format()}");
        Assert.That(
            diag!.GetMessage(),
            Does.Contain(expectedSubstring),
            $"Expected {id} message to name the full field path '{expectedSubstring}', got:\n{diag.GetMessage()}"
        );
    }

    static void AssertNoDiagnostic(string source, string forbiddenId)
    {
        var run = GeneratorTestHarness.Run(
            new IIncrementalGenerator[]
            {
                new TemplateDefinitionGenerator(),
                new EntityComponentGenerator(),
                new VariableUpdateOnlyValidator(),
            },
            source
        );
        var diag = run.GenDiagnostics.FirstOrDefault(d => d.Id == forbiddenId);
        Assert.That(diag, Is.Null, $"Did not expect {forbiddenId}, got:\n{run.Format()}");
    }
}
