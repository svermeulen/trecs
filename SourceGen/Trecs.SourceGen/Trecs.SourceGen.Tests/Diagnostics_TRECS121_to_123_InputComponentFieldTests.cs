using System.Linq;
using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Negative tests for the [Input]-component field-type rules (TRECS121-123).
/// All emitted from TemplateValidator: an input component cannot embed a persistent
/// pointer (it would leak when the input frame is retired), cannot embed TrecsList
/// (chunk-store backed; not supported on inputs), and a Retain-mode input cannot
/// embed an InputXxxPtr (the previous frame's slot was already released).
/// </summary>
[TestFixture]
public class Diagnostics_TRECS121_to_123_InputComponentFieldTests
{
    [Test]
    public void TRECS121_NativeUniquePtr_InInputComponent_Fires()
    {
        const string source = """
            namespace Sample
            {
                public partial struct CCmd : Trecs.IEntityComponent
                {
                    public Trecs.NativeUniquePtr<int> Payload;
                }

                public partial class PlayerTemplate : Trecs.ITemplate
                {
                    [Trecs.Input(Trecs.MissingInputBehavior.Reset)]
                    CCmd Command;
                }
            }
            """;

        AssertDiagnostic(source, "TRECS121");
    }

    [Test]
    public void TRECS121_SharedPtr_InInputComponent_Fires()
    {
        const string source = """
            namespace Sample
            {
                public class MyBlob { }

                public partial struct CCmd : Trecs.IEntityComponent
                {
                    public Trecs.SharedPtr<MyBlob> Blob;
                }

                public partial class PlayerTemplate : Trecs.ITemplate
                {
                    [Trecs.Input(Trecs.MissingInputBehavior.Reset)]
                    CCmd Command;
                }
            }
            """;

        AssertDiagnostic(source, "TRECS121");
    }

    [Test]
    public void TRECS121_PersistentPtr_OnNonInputField_DoesNotFire()
    {
        // The rule only applies to [Input] fields. A persistent ptr embedded in
        // a regular (non-input) component is fine — the user is responsible for
        // disposing it as part of normal heap lifecycle.
        const string source = """
            namespace Sample
            {
                public partial struct CState : Trecs.IEntityComponent
                {
                    public Trecs.NativeUniquePtr<int> Payload;
                }

                public partial class PlayerTemplate : Trecs.ITemplate
                {
                    CState State;
                }
            }
            """;

        AssertNoDiagnostic(source, "TRECS121");
    }

    [Test]
    public void TRECS121_InputNativeUniquePtr_DoesNotFire()
    {
        // Positive control: the migration target (the Input* variant) must not
        // itself trip TRECS121.
        const string source = """
            namespace Sample
            {
                public partial struct CCmd : Trecs.IEntityComponent
                {
                    public Trecs.InputNativeUniquePtr<int> Payload;
                }

                public partial class PlayerTemplate : Trecs.ITemplate
                {
                    [Trecs.Input(Trecs.MissingInputBehavior.Reset)]
                    CCmd Command;
                }
            }
            """;

        AssertNoDiagnostic(source, "TRECS121");
    }

    [Test]
    public void TRECS122_TrecsList_InInputComponent_Fires()
    {
        const string source = """
            namespace Sample
            {
                public partial struct CCmd : Trecs.IEntityComponent
                {
                    public Trecs.TrecsList<int> Items;
                }

                public partial class PlayerTemplate : Trecs.ITemplate
                {
                    [Trecs.Input(Trecs.MissingInputBehavior.Reset)]
                    CCmd Command;
                }
            }
            """;

        AssertDiagnostic(source, "TRECS122");
    }

    [Test]
    public void TRECS122_TrecsList_OnNonInputField_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                public partial struct CState : Trecs.IEntityComponent
                {
                    public Trecs.TrecsList<int> Items;
                }

                public partial class PlayerTemplate : Trecs.ITemplate
                {
                    CState State;
                }
            }
            """;

        AssertNoDiagnostic(source, "TRECS122");
    }

    [Test]
    public void TRECS123_RetainPlusInputNativeUniquePtr_Fires()
    {
        const string source = """
            namespace Sample
            {
                public partial struct CCmd : Trecs.IEntityComponent
                {
                    public Trecs.InputNativeUniquePtr<int> Payload;
                }

                public partial class PlayerTemplate : Trecs.ITemplate
                {
                    [Trecs.Input(Trecs.MissingInputBehavior.Retain)]
                    CCmd Command;
                }
            }
            """;

        AssertDiagnostic(source, "TRECS123");
    }

    [Test]
    public void TRECS123_RetainPlusInputSharedPtr_Fires()
    {
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
                    [Trecs.Input(Trecs.MissingInputBehavior.Retain)]
                    CCmd Command;
                }
            }
            """;

        AssertDiagnostic(source, "TRECS123");
    }

    [Test]
    public void TRECS123_ResetPlusInputPtr_DoesNotFire()
    {
        // Reset mode discards the component when no input arrives, so there's no
        // dangling pointer issue. This is the supported pairing.
        const string source = """
            namespace Sample
            {
                public partial struct CCmd : Trecs.IEntityComponent
                {
                    public Trecs.InputNativeUniquePtr<int> Payload;
                }

                public partial class PlayerTemplate : Trecs.ITemplate
                {
                    [Trecs.Input(Trecs.MissingInputBehavior.Reset)]
                    CCmd Command;
                }
            }
            """;

        AssertNoDiagnostic(source, "TRECS123");
    }

    [Test]
    public void TRECS123_RetainWithoutInputPtr_DoesNotFire()
    {
        // Retain alone is fine — the danger only arises when the component
        // carries an InputXxxPtr that would be left dangling.
        const string source = """
            namespace Sample
            {
                public partial struct CCmd : Trecs.IEntityComponent
                {
                    public int V;
                }

                public partial class PlayerTemplate : Trecs.ITemplate
                {
                    [Trecs.Input(Trecs.MissingInputBehavior.Retain)]
                    CCmd Command;
                }
            }
            """;

        AssertNoDiagnostic(source, "TRECS123");
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
