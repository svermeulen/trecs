using System.Collections.Generic;
using NUnit.Framework;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    static class TestShutdownLog
    {
        public static readonly List<string> ReadyOrder = new();
        public static readonly List<string> ShutdownOrder = new();
        public static int WorldReadableInsideShutdown;

        public static void Clear()
        {
            ReadyOrder.Clear();
            ShutdownOrder.Clear();
            WorldReadableInsideShutdown = 0;
        }
    }

    [ExecuteIn(SystemPhase.Input)]
    partial class ShutdownInputSystem : ISystem
    {
        public void Execute() { }

        partial void OnReady() => TestShutdownLog.ReadyOrder.Add("Input");

        partial void OnShutdown() => TestShutdownLog.ShutdownOrder.Add("Input");
    }

    [ExecuteIn(SystemPhase.Fixed)]
    partial class ShutdownFixedSystem : ISystem
    {
        public void Execute() { }

        partial void OnReady() => TestShutdownLog.ReadyOrder.Add("Fixed");

        partial void OnShutdown() => TestShutdownLog.ShutdownOrder.Add("Fixed");
    }

    [ExecuteIn(SystemPhase.EarlyPresentation)]
    partial class ShutdownEarlyPresSystem : ISystem
    {
        public void Execute() { }

        partial void OnReady() => TestShutdownLog.ReadyOrder.Add("EarlyPres");

        partial void OnShutdown() => TestShutdownLog.ShutdownOrder.Add("EarlyPres");
    }

    [ExecuteIn(SystemPhase.Presentation)]
    partial class ShutdownPresSystem : ISystem
    {
        public void Execute() { }

        partial void OnReady() => TestShutdownLog.ReadyOrder.Add("Pres");

        partial void OnShutdown() => TestShutdownLog.ShutdownOrder.Add("Pres");
    }

    [ExecuteIn(SystemPhase.LatePresentation)]
    partial class ShutdownLatePresSystem : ISystem
    {
        public void Execute() { }

        partial void OnReady() => TestShutdownLog.ReadyOrder.Add("LatePres");

        partial void OnShutdown() => TestShutdownLog.ShutdownOrder.Add("LatePres");
    }

    partial class ShutdownFixedA : ISystem
    {
        public void Execute() { }

        partial void OnReady() => TestShutdownLog.ReadyOrder.Add("A");

        partial void OnShutdown() => TestShutdownLog.ShutdownOrder.Add("A");
    }

    [ExecuteAfter(typeof(ShutdownFixedA))]
    partial class ShutdownFixedB : ISystem
    {
        public void Execute() { }

        partial void OnReady() => TestShutdownLog.ReadyOrder.Add("B");

        partial void OnShutdown() => TestShutdownLog.ShutdownOrder.Add("B");
    }

    partial class ShutdownReadsWorldSystem : ISystem
    {
        public void Execute() { }

        partial void OnShutdown()
        {
            // The world must still be functional inside OnShutdown — counts and
            // queries must not throw. Record success so the test can assert it.
            var allGroups = World.WorldInfo.AllGroups;
            var groupCount = allGroups.Count;
            TestShutdownLog.WorldReadableInsideShutdown = groupCount > 0 ? 1 : -1;
        }
    }

    [TestFixture]
    public class SystemShutdownTests
    {
        TestEnvironment CreateEnvWithSystems(params ISystem[] systems)
        {
            var builder = new WorldBuilder()
                .SetSettings(new WorldSettings())
                .AddTemplate(TrecsTemplates.Globals.Template)
                .AddTemplate(TestTemplates.SimpleAlpha)
                .AddBlobStore(EcsTestHelper.CreateBlobStore());

            var world = builder.Build();

            foreach (var system in systems)
            {
                world.AddSystem(system);
            }

            world.Initialize();
            return new TestEnvironment(world);
        }

        [SetUp]
        public void SetUp()
        {
            TestShutdownLog.Clear();
        }

        [Test]
        public void OnShutdown_RunsOncePerSystem_InReverseOrderOfOnReady()
        {
            var env = CreateEnvWithSystems(
                new ShutdownInputSystem(),
                new ShutdownFixedSystem(),
                new ShutdownEarlyPresSystem(),
                new ShutdownPresSystem(),
                new ShutdownLatePresSystem()
            );

            // OnReady fired during Initialize. Capture and clear so we only assert
            // on shutdown order below.
            var readyOrder = new List<string>(TestShutdownLog.ReadyOrder);

            env.Dispose();

            CollectionAssert.AreEqual(
                new[] { "EarlyPres", "Input", "Fixed", "Pres", "LatePres" },
                readyOrder
            );
            CollectionAssert.AreEqual(
                new[] { "LatePres", "Pres", "Fixed", "Input", "EarlyPres" },
                TestShutdownLog.ShutdownOrder
            );
        }

        [Test]
        public void OnShutdown_WithinPhase_RunsInReverseOfReadyOrder()
        {
            // ExecuteAfter orders B after A at OnReady time. OnShutdown must reverse it.
            var env = CreateEnvWithSystems(new ShutdownFixedA(), new ShutdownFixedB());

            var readyOrder = new List<string>(TestShutdownLog.ReadyOrder);

            env.Dispose();

            CollectionAssert.AreEqual(new[] { "A", "B" }, readyOrder);
            CollectionAssert.AreEqual(new[] { "B", "A" }, TestShutdownLog.ShutdownOrder);
        }

        [Test]
        public void OnShutdown_WorldStillReadable()
        {
            var env = CreateEnvWithSystems(new ShutdownReadsWorldSystem());

            env.Dispose();

            NAssert.AreEqual(
                1,
                TestShutdownLog.WorldReadableInsideShutdown,
                "Expected World to be queryable inside OnShutdown."
            );
        }

        [Test]
        public void OnShutdown_NotCalled_IfInitializeNeverRan()
        {
            // World built but never initialized — _initializeCompleted is false,
            // so OnShutdown must be skipped (no system has had a chance to set up).
            var builder = new WorldBuilder()
                .SetSettings(new WorldSettings())
                .AddTemplate(TrecsTemplates.Globals.Template)
                .AddTemplate(TestTemplates.SimpleAlpha)
                .AddBlobStore(EcsTestHelper.CreateBlobStore());

            var world = builder.Build();
            world.AddSystem(new ShutdownInputSystem());

            world.Dispose();

            CollectionAssert.IsEmpty(TestShutdownLog.ReadyOrder);
            CollectionAssert.IsEmpty(TestShutdownLog.ShutdownOrder);
        }
    }
}
