using System.Linq;
using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace Trecs.SourceGen.Tests;

/// <summary>
/// Negative tests for the AutoSystem wiring diagnostics group (TRECS040-047). All
/// emitted from AutoSystemGenerator's validator when an ISystem class violates a
/// wiring rule.
///
/// Codes covered: 040, 043, 044, 047.
///
/// Codes intentionally not covered here:
/// - TRECS041, TRECS042, TRECS045, TRECS046: gaps in numbering.
/// </summary>
[TestFixture]
public class Diagnostics_TRECS040_to_047_AutoSystemTests
{
    [Test]
    public void TRECS040_AutoSystemNotPartial()
    {
        // ISystem classes with iteration methods need to be partial — the generator
        // emits the World wiring + Execute routing onto them.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public struct PlayerTag : Trecs.ITag { }

                public class MySystem : Trecs.ISystem
                {
                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    void Process(in CPos pos) { }

                    public void Execute() { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS040");
    }

    [Test]
    public void TRECS043_ExecuteIterationMethodWithCustomParams()
    {
        // A method named Execute is the ISystem entry point — it can't take user-supplied
        // (pass-through) custom parameters, since those would have no value to pass in.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public struct PlayerTag : Trecs.ITag { }

                public partial class MySystem : Trecs.ISystem
                {
                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    void Execute(in CPos pos, [Trecs.PassThroughArgument] float dt) { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS043");
    }

    [Test]
    public void TRECS044_ExecuteConflictUserAndIteration()
    {
        // Having both a user-defined `public void Execute()` and a method named Execute
        // marked [ForEachEntity] is ambiguous — both want to be the ISystem entry point.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public struct PlayerTag : Trecs.ITag { }

                public partial class MySystem : Trecs.ISystem
                {
                    public void Execute() { }

                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    void Execute(in CPos pos) { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS044");
    }

    [Test]
    public void TRECS047_SystemHasIterationsButNoExecute()
    {
        // If a class has [ForEachEntity] methods but no entry point — neither a
        // user-defined Execute() nor an iteration method named Execute — the generated
        // ISystem.Execute() has nothing to dispatch to.
        const string source = """
            namespace Sample
            {
                public partial struct CPos : Trecs.IEntityComponent { public float X; }
                public struct PlayerTag : Trecs.ITag { }

                public partial class MySystem : Trecs.ISystem
                {
                    [Trecs.ForEachEntity(Tag = typeof(PlayerTag))]
                    void DoStuff(in CPos pos) { }
                }
            }
            """;

        AssertDiagnostic(source, "TRECS047");
    }

    static void AssertDiagnostic(string source, string expectedId)
    {
        var run = GeneratorTestHarness.Run(
            new IIncrementalGenerator[]
            {
                new AutoSystemGenerator(),
                new ForEachGenerator(),
                new EntityComponentGenerator(),
            },
            source
        );
        var diag = run.GenDiagnostics.FirstOrDefault(d => d.Id == expectedId);
        Assert.That(
            diag,
            Is.Not.Null,
            $"Expected {expectedId}, got:\n{run.Format()}"
        );
    }
}
