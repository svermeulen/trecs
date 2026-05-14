using System;
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
        public static int NonGlobalTagCountInsideShutdown = -1;
        public static int NonGlobalQueryCountInsideShutdown = -1;
        public static int GlobalReadValueInsideShutdown = int.MinValue;
        public static int GlobalReadBackAfterWriteInsideShutdown = int.MinValue;
        public static int GlobalGroupOnRemovedCalls;
        public static IDisposable GlobalGroupOnRemovedSubscription;

        public static void Clear()
        {
            ReadyOrder.Clear();
            ShutdownOrder.Clear();
            WorldReadableInsideShutdown = 0;
            NonGlobalTagCountInsideShutdown = -1;
            NonGlobalQueryCountInsideShutdown = -1;
            GlobalReadValueInsideShutdown = int.MinValue;
            GlobalReadBackAfterWriteInsideShutdown = int.MinValue;
            GlobalGroupOnRemovedCalls = 0;
            GlobalGroupOnRemovedSubscription?.Dispose();
            GlobalGroupOnRemovedSubscription = null;
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
            // World schema (groups, templates) is unaffected by RemoveAllEntities and
            // remains readable inside OnShutdown. The global entity also remains live;
            // only non-global entity queries return empty here.
            var allGroups = World.WorldInfo.AllGroups;
            var groupCount = allGroups.Count;
            TestShutdownLog.WorldReadableInsideShutdown = groupCount > 0 ? 1 : -1;
        }
    }

    partial class ShutdownNonGlobalQueryCheckSystem : ISystem
    {
        public void Execute() { }

        partial void OnShutdown()
        {
            // RemoveAllEntities ran just before this. Non-global queries must see an empty world.
            TestShutdownLog.NonGlobalTagCountInsideShutdown = World.CountEntitiesWithTags(
                TestTags.Alpha
            );
            TestShutdownLog.NonGlobalQueryCountInsideShutdown = World
                .Query()
                .WithTags(TestTags.Alpha)
                .Count();
        }
    }

    partial class ShutdownGlobalMutationSystem : ISystem
    {
        public void Execute() { }

        partial void OnShutdown()
        {
            // Global entity remains queryable AND mutable in OnShutdown.
            ref readonly var initial = ref World.GlobalComponent<TestGlobalInt>().Read;
            TestShutdownLog.GlobalReadValueInsideShutdown = initial.Value;

            ref var writable = ref World.GlobalComponent<TestGlobalInt>().Write;
            writable.Value = 999;

            ref readonly var readBack = ref World.GlobalComponent<TestGlobalInt>().Read;
            TestShutdownLog.GlobalReadBackAfterWriteInsideShutdown = readBack.Value;
        }
    }

    partial class ShutdownGlobalGroupOnRemovedSystem : ISystem
    {
        public void Execute() { }

        partial void OnReady()
        {
            TestShutdownLog.GlobalGroupOnRemovedSubscription = World
                .Events.InGroup(World.WorldInfo.GlobalGroup)
                .OnRemoved(
                    (GroupIndex _, EntityRange _) => TestShutdownLog.GlobalGroupOnRemovedCalls++
                );
        }
    }

    [TestFixture]
    public class SystemShutdownTests
    {
        TestEnvironment CreateEnvWithSystems(params ISystem[] systems) =>
            CreateEnvWithSystems(TrecsTemplates.Globals.Template, systems);

        TestEnvironment CreateEnvWithSystems(Template globalsTemplate, params ISystem[] systems)
        {
            var builder = new WorldBuilder()
                .SetSettings(new WorldSettings())
                .AddTemplate(globalsTemplate)
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
        public void OnShutdown_NonGlobalQueries_ReturnEmpty()
        {
            var env = CreateEnvWithSystems(new ShutdownNonGlobalQueryCheckSystem());

            // Spawn a non-global entity so queries would return >0 if RemoveAllEntities
            // hadn't logically cleared the world before the shutdown hook ran.
            var a = env.Accessor;
            a.AddEntity(TestTags.Alpha).AssertComplete();
            a.SubmitEntities();
            NAssert.AreEqual(1, a.CountEntitiesWithTags(TestTags.Alpha));

            env.Dispose();

            NAssert.AreEqual(
                0,
                TestShutdownLog.NonGlobalTagCountInsideShutdown,
                "CountEntitiesWithTags should report 0 inside OnShutdown after RemoveAllEntities ran."
            );
            NAssert.AreEqual(
                0,
                TestShutdownLog.NonGlobalQueryCountInsideShutdown,
                "Query()...Count() should report 0 inside OnShutdown after RemoveAllEntities ran."
            );
        }

        [Test]
        public void OnShutdown_GlobalEntity_RemainsQueryableAndMutable()
        {
            var env = CreateEnvWithSystems(
                TestGlobalsTemplate.Template,
                new ShutdownGlobalMutationSystem()
            );

            // Seed the global component so we can assert the OnShutdown read sees it.
            env.Accessor.GlobalComponent<TestGlobalInt>().Write.Value = 7;

            env.Dispose();

            NAssert.AreEqual(
                7,
                TestShutdownLog.GlobalReadValueInsideShutdown,
                "Global component read inside OnShutdown should see the value written before dispose."
            );
            NAssert.AreEqual(
                999,
                TestShutdownLog.GlobalReadBackAfterWriteInsideShutdown,
                "Global component must remain mutable inside OnShutdown."
            );
        }

        [Test]
        public void OnShutdown_GlobalGroupOnRemoved_DoesNotFire()
        {
            var env = CreateEnvWithSystems(new ShutdownGlobalGroupOnRemovedSystem());

            env.Dispose();

            NAssert.AreEqual(
                0,
                TestShutdownLog.GlobalGroupOnRemovedCalls,
                "OnRemoved registered against the global group must not fire during world disposal."
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
