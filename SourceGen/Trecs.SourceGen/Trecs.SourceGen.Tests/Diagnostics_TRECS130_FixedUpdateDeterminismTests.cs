using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using NUnit.Framework;

namespace Trecs.SourceGen.Tests;

[TestFixture]
public class Diagnostics_TRECS130_FixedUpdateDeterminismTests
{
    // ── DateTime ───────────────────────────────────────────────────────

    [Test]
    public void DateTimeNow_InFixedSystem_Fires()
    {
        const string source = """
            namespace Sample
            {
                partial class MySystem : Trecs.ISystem
                {
                    public void Execute()
                    {
                        var t = System.DateTime.Now;
                    }
                }
            }
            """;

        AssertFires(source);
    }

    [Test]
    public void DateTimeUtcNow_InFixedSystem_Fires()
    {
        const string source = """
            namespace Sample
            {
                partial class MySystem : Trecs.ISystem
                {
                    public void Execute()
                    {
                        var t = System.DateTime.UtcNow;
                    }
                }
            }
            """;

        AssertFires(source);
    }

    [Test]
    public void DateTimeNow_InPresentationSystem_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.ExecuteIn(Trecs.SystemPhase.Presentation)]
                partial class MySystem : Trecs.ISystem
                {
                    public void Execute()
                    {
                        var t = System.DateTime.Now;
                    }
                }
            }
            """;

        AssertDoesNotFire(source);
    }

    [Test]
    public void DateTimeNow_InNonSystem_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                public class NotASystem
                {
                    public void Run()
                    {
                        var t = System.DateTime.Now;
                    }
                }
            }
            """;

        AssertDoesNotFire(source);
    }

    // ── UnityEngine.Time ───────────────────────────────────────────────

    [Test]
    public void TimeTime_InFixedSystem_Fires()
    {
        const string source = """
            namespace Sample
            {
                partial class MySystem : Trecs.ISystem
                {
                    public void Execute()
                    {
                        var t = UnityEngine.Time.time;
                    }
                }
            }
            """;

        AssertFires(source);
    }

    [Test]
    public void TimeDeltaTime_InFixedSystem_Fires()
    {
        const string source = """
            namespace Sample
            {
                partial class MySystem : Trecs.ISystem
                {
                    public void Execute()
                    {
                        var dt = UnityEngine.Time.deltaTime;
                    }
                }
            }
            """;

        AssertFires(source);
    }

    [Test]
    public void TimeUnscaledTime_InFixedSystem_Fires()
    {
        const string source = """
            namespace Sample
            {
                partial class MySystem : Trecs.ISystem
                {
                    public void Execute()
                    {
                        var t = UnityEngine.Time.unscaledTime;
                    }
                }
            }
            """;

        AssertFires(source);
    }

    [Test]
    public void TimeRealtimeSinceStartup_InFixedSystem_Fires()
    {
        const string source = """
            namespace Sample
            {
                partial class MySystem : Trecs.ISystem
                {
                    public void Execute()
                    {
                        var t = UnityEngine.Time.realtimeSinceStartup;
                    }
                }
            }
            """;

        AssertFires(source);
    }

    [Test]
    public void TimeDeltaTime_InPresentationSystem_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.ExecuteIn(Trecs.SystemPhase.Presentation)]
                partial class MySystem : Trecs.ISystem
                {
                    public void Execute()
                    {
                        var dt = UnityEngine.Time.deltaTime;
                    }
                }
            }
            """;

        AssertDoesNotFire(source);
    }

    [Test]
    public void TimeDeltaTime_InEarlyPresentationSystem_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.ExecuteIn(Trecs.SystemPhase.EarlyPresentation)]
                partial class MySystem : Trecs.ISystem
                {
                    public void Execute()
                    {
                        var dt = UnityEngine.Time.deltaTime;
                    }
                }
            }
            """;

        AssertDoesNotFire(source);
    }

    [Test]
    public void TimeDeltaTime_InInputSystem_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.ExecuteIn(Trecs.SystemPhase.Input)]
                partial class MySystem : Trecs.ISystem
                {
                    public void Execute()
                    {
                        var dt = UnityEngine.Time.deltaTime;
                    }
                }
            }
            """;

        AssertDoesNotFire(source);
    }

    // ── System.Random ──────────────────────────────────────────────────

    [Test]
    public void NewSystemRandom_InFixedSystem_Fires()
    {
        const string source = """
            namespace Sample
            {
                partial class MySystem : Trecs.ISystem
                {
                    public void Execute()
                    {
                        var rng = new System.Random();
                    }
                }
            }
            """;

        AssertFires(source);
    }

    [Test]
    public void SystemRandomNext_InFixedSystem_Fires()
    {
        const string source = """
            namespace Sample
            {
                partial class MySystem : Trecs.ISystem
                {
                    System.Random _rng = new System.Random(42);

                    public void Execute()
                    {
                        var val = _rng.Next();
                    }
                }
            }
            """;

        AssertFires(source);
    }

    [Test]
    public void NewSystemRandom_InPresentationSystem_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.ExecuteIn(Trecs.SystemPhase.Presentation)]
                partial class MySystem : Trecs.ISystem
                {
                    public void Execute()
                    {
                        var rng = new System.Random();
                    }
                }
            }
            """;

        AssertDoesNotFire(source);
    }

    // ── UnityEngine.Random ─────────────────────────────────────────────

    [Test]
    public void UnityRandomRange_InFixedSystem_Fires()
    {
        const string source = """
            namespace Sample
            {
                partial class MySystem : Trecs.ISystem
                {
                    public void Execute()
                    {
                        var val = UnityEngine.Random.Range(0f, 1f);
                    }
                }
            }
            """;

        AssertFires(source);
    }

    [Test]
    public void UnityRandomValue_InFixedSystem_Fires()
    {
        const string source = """
            namespace Sample
            {
                partial class MySystem : Trecs.ISystem
                {
                    public void Execute()
                    {
                        var val = UnityEngine.Random.value;
                    }
                }
            }
            """;

        AssertFires(source);
    }

    [Test]
    public void UnityRandomRange_InPresentationSystem_DoesNotFire()
    {
        const string source = """
            namespace Sample
            {
                [Trecs.ExecuteIn(Trecs.SystemPhase.Presentation)]
                partial class MySystem : Trecs.ISystem
                {
                    public void Execute()
                    {
                        var val = UnityEngine.Random.Range(0, 10);
                    }
                }
            }
            """;

        AssertDoesNotFire(source);
    }

    // ── Default phase (no attribute = Fixed) ───────────────────────────

    [Test]
    public void DefaultPhase_IsFixed_Fires()
    {
        const string source = """
            namespace Sample
            {
                partial class MySystem : Trecs.ISystem
                {
                    public void Execute()
                    {
                        var t = System.DateTime.Now;
                        var dt = UnityEngine.Time.deltaTime;
                        var rng = new System.Random();
                        var val = UnityEngine.Random.Range(0f, 1f);
                    }
                }
            }
            """;

        var diagnostics = RunAnalyzer(source);
        var hits = diagnostics.Where(d => d.Id == "TRECS130").ToList();
        Assert.That(hits.Count, Is.EqualTo(4), $"Expected 4 hits, got:\n{Format(diagnostics)}");
    }

    // ── Helper methods ─────────────────────────────────────────────────

    static void AssertFires(string source)
    {
        var diagnostics = RunAnalyzer(source);
        var diag = diagnostics.FirstOrDefault(d => d.Id == "TRECS130");
        Assert.That(diag, Is.Not.Null, $"Expected TRECS130, got:\n{Format(diagnostics)}");
    }

    static void AssertDoesNotFire(string source)
    {
        var diagnostics = RunAnalyzer(source);
        var hit = diagnostics.FirstOrDefault(d => d.Id == "TRECS130");
        Assert.That(hit, Is.Null, $"Unexpected TRECS130: {hit}");
    }

    static ImmutableArray<Diagnostic> RunAnalyzer(string source)
    {
        return GeneratorTestHarness.RunAnalyzers(
            new DiagnosticAnalyzer[] { new FixedUpdateDeterminismAnalyzer() },
            source
        );
    }

    static string Format(ImmutableArray<Diagnostic> diagnostics)
    {
        if (diagnostics.IsEmpty)
            return "  (none)";
        return string.Join(
            "\n",
            diagnostics.Select(d =>
                $"  {d.Severity} {d.Id} at {d.Location.GetLineSpan()}: {d.GetMessage()}"
            )
        );
    }
}
