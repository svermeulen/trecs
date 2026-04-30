using System.Collections.Generic;
using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Shared state for test systems that record execution order.
    /// Not itself a system — avoids source gen generating a duplicate World property.
    /// </summary>
    static class TestSystemLog
    {
        public static readonly List<string> ExecutionLog = new();

        public static void Clear() => ExecutionLog.Clear();
    }

    partial class SystemA : ISystem
    {
        public void Execute() => TestSystemLog.ExecutionLog.Add("A");
    }

    partial class SystemB : ISystem
    {
        public void Execute() => TestSystemLog.ExecutionLog.Add("B");
    }

    partial class SystemC : ISystem
    {
        public void Execute() => TestSystemLog.ExecutionLog.Add("C");
    }

    [ExecuteAfter(typeof(SystemA))]
    partial class SystemAfterA : ISystem
    {
        public void Execute() => TestSystemLog.ExecutionLog.Add("AfterA");
    }

    [ExecuteBefore(typeof(SystemA))]
    partial class SystemBeforeA : ISystem
    {
        public void Execute() => TestSystemLog.ExecutionLog.Add("BeforeA");
    }

    [ExecuteAfter(typeof(SystemA))]
    [ExecuteBefore(typeof(SystemC))]
    partial class SystemBetweenAC : ISystem
    {
        public void Execute() => TestSystemLog.ExecutionLog.Add("BetweenAC");
    }

    [Phase(SystemPhase.Presentation)]
    partial class VariableSystemA : ISystem
    {
        public void Execute() => TestSystemLog.ExecutionLog.Add("VarA");
    }

    [Phase(SystemPhase.Presentation)]
    partial class VariableSystemB : ISystem
    {
        public void Execute() => TestSystemLog.ExecutionLog.Add("VarB");
    }

    [Phase(SystemPhase.Presentation)]
    [ExecuteAfter(typeof(VariableSystemA))]
    partial class VariableSystemAfterA : ISystem
    {
        public void Execute() => TestSystemLog.ExecutionLog.Add("VarAfterA");
    }

    [Phase(SystemPhase.LatePresentation)]
    partial class LateVarSystem : ISystem
    {
        public void Execute() => TestSystemLog.ExecutionLog.Add("LateVar");
    }

    [ExecutePriority(10)]
    [Phase(SystemPhase.Presentation)]
    partial class PrioritySystem10 : ISystem
    {
        public void Execute() => TestSystemLog.ExecutionLog.Add("P10");
    }

    [ExecutePriority(20)]
    [Phase(SystemPhase.Presentation)]
    partial class PrioritySystem20 : ISystem
    {
        public void Execute() => TestSystemLog.ExecutionLog.Add("P20");
    }

    [ExecutePriority(5)]
    [Phase(SystemPhase.Presentation)]
    partial class PrioritySystem5 : ISystem
    {
        public void Execute() => TestSystemLog.ExecutionLog.Add("P5");
    }

    [Phase(SystemPhase.Presentation)]
    partial class EnableDisableSystem : ISystem
    {
        public int ExecuteCount;

        public void Execute()
        {
            ExecuteCount++;
            TestSystemLog.ExecutionLog.Add("EnableDisable");
        }
    }

    [Phase(SystemPhase.Presentation)]
    partial class WorldAccessSystem : ISystem
    {
        public bool HasAccessor;

        public void Execute()
        {
            HasAccessor = World != null;
            TestSystemLog.ExecutionLog.Add("WorldAccess");
        }
    }

    [TestFixture]
    public class SystemExecutionTests
    {
        TestEnvironment CreateEnvWithSystems(params ISystem[] systems)
        {
            var builder = new WorldBuilder()
                .SetSettings(new WorldSettings())
                .AddEntityType(TrecsTemplates.Globals.Template)
                .AddEntityType(TestTemplates.SimpleAlpha)
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
            TestSystemLog.Clear();
        }

        #region ExecuteAfter / ExecuteBefore

        [Test]
        public void System_ExecutesAfter_RunsInOrder()
        {
            using var env = CreateEnvWithSystems(new SystemAfterA(), new SystemA());

            env.World.Tick();
            env.World.LateTick();

            int indexA = TestSystemLog.ExecutionLog.IndexOf("A");
            int indexAfterA = TestSystemLog.ExecutionLog.IndexOf("AfterA");

            NAssert.IsTrue(indexA >= 0, "SystemA should have executed");
            NAssert.IsTrue(indexAfterA >= 0, "SystemAfterA should have executed");
            NAssert.Less(indexA, indexAfterA, "SystemA should execute before SystemAfterA");
        }

        [Test]
        public void System_ExecutesBefore_RunsInOrder()
        {
            using var env = CreateEnvWithSystems(new SystemA(), new SystemBeforeA());

            env.World.Tick();
            env.World.LateTick();

            int indexA = TestSystemLog.ExecutionLog.IndexOf("A");
            int indexBeforeA = TestSystemLog.ExecutionLog.IndexOf("BeforeA");

            NAssert.IsTrue(indexA >= 0, "SystemA should have executed");
            NAssert.IsTrue(indexBeforeA >= 0, "SystemBeforeA should have executed");
            NAssert.Less(indexBeforeA, indexA, "SystemBeforeA should execute before SystemA");
        }

        [Test]
        public void System_Chain_ABC_Ordered()
        {
            using var env = CreateEnvWithSystems(
                new SystemC(),
                new SystemBetweenAC(),
                new SystemA()
            );

            env.World.Tick();
            env.World.LateTick();

            int indexA = TestSystemLog.ExecutionLog.IndexOf("A");
            int indexBetween = TestSystemLog.ExecutionLog.IndexOf("BetweenAC");
            int indexC = TestSystemLog.ExecutionLog.IndexOf("C");

            NAssert.IsTrue(indexA >= 0 && indexBetween >= 0 && indexC >= 0);
            NAssert.Less(indexA, indexBetween, "A before BetweenAC");
            NAssert.Less(indexBetween, indexC, "BetweenAC before C");
        }

        #endregion

        #region Variable vs Fixed Phase Segregation

        [Test]
        public void System_VariableUpdate_RunsOnTick()
        {
            using var env = CreateEnvWithSystems(new VariableSystemA());

            env.World.Tick();
            env.World.LateTick();

            NAssert.Contains("VarA", TestSystemLog.ExecutionLog);
        }

        [Test]
        public void System_VariableOrdering_Preserved()
        {
            using var env = CreateEnvWithSystems(new VariableSystemAfterA(), new VariableSystemA());

            env.World.Tick();
            env.World.LateTick();

            int indexVarA = TestSystemLog.ExecutionLog.IndexOf("VarA");
            int indexVarAfterA = TestSystemLog.ExecutionLog.IndexOf("VarAfterA");

            NAssert.IsTrue(indexVarA >= 0 && indexVarAfterA >= 0);
            NAssert.Less(indexVarA, indexVarAfterA);
        }

        #endregion

        #region LateVariable Phase

        [Test]
        public void System_LateVariable_RunsOnLateTick()
        {
            using var env = CreateEnvWithSystems(new VariableSystemA(), new LateVarSystem());

            // Tick runs variable systems
            env.World.Tick();

            var logAfterTick = new List<string>(TestSystemLog.ExecutionLog);
            NAssert.Contains("VarA", logAfterTick, "Variable system runs on Tick");
            NAssert.IsFalse(logAfterTick.Contains("LateVar"), "LateVar should not run on Tick");

            // LateTick runs late variable systems
            env.World.LateTick();

            NAssert.Contains(
                "LateVar",
                TestSystemLog.ExecutionLog,
                "LateVar should run on LateTick"
            );

            // Verify ordering: VarA before LateVar
            int indexVar = TestSystemLog.ExecutionLog.IndexOf("VarA");
            int indexLate = TestSystemLog.ExecutionLog.IndexOf("LateVar");
            NAssert.Less(indexVar, indexLate);
        }

        #endregion

        #region ExecutePriority

        [Test]
        public void System_ExecutePriority_OrderedByPriority()
        {
            using var env = CreateEnvWithSystems(
                new PrioritySystem20(),
                new PrioritySystem5(),
                new PrioritySystem10()
            );

            env.World.Tick();
            env.World.LateTick();

            int idx5 = TestSystemLog.ExecutionLog.IndexOf("P5");
            int idx10 = TestSystemLog.ExecutionLog.IndexOf("P10");
            int idx20 = TestSystemLog.ExecutionLog.IndexOf("P20");

            NAssert.IsTrue(idx5 >= 0 && idx10 >= 0 && idx20 >= 0);
            NAssert.Less(idx5, idx10, "Priority 5 before Priority 10");
            NAssert.Less(idx10, idx20, "Priority 10 before Priority 20");
        }

        #endregion

        #region System Execute Count

        [Test]
        public void System_ExecutesOnEachTick()
        {
            var system = new EnableDisableSystem();
            using var env = CreateEnvWithSystems(system);

            env.World.Tick();
            env.World.LateTick();
            NAssert.AreEqual(1, system.ExecuteCount);

            env.World.Tick();
            env.World.LateTick();
            NAssert.AreEqual(2, system.ExecuteCount);

            env.World.Tick();
            env.World.LateTick();
            NAssert.AreEqual(3, system.ExecuteCount);
        }

        #endregion

        #region System Can Access World

        [Test]
        public void System_HasWorldAccessor_DuringExecute()
        {
            var system = new WorldAccessSystem();
            using var env = CreateEnvWithSystems(system);

            env.World.Tick();
            env.World.LateTick();

            NAssert.IsTrue(system.HasAccessor, "System should have WorldAccessor during Execute");
        }

        #endregion

        #region Multiple Systems Same Phase

        [Test]
        public void System_MultipleVariable_AllExecute()
        {
            using var env = CreateEnvWithSystems(new VariableSystemA(), new VariableSystemB());

            env.World.Tick();
            env.World.LateTick();

            NAssert.Contains("VarA", TestSystemLog.ExecutionLog);
            NAssert.Contains("VarB", TestSystemLog.ExecutionLog);
        }

        #endregion

        #region Pause

        [Test]
        public void System_WorldPaused_NoSystemsExecuteOnTick()
        {
            using var env = CreateEnvWithSystems(new VariableSystemA());
            var runner = env.World.GetSystemRunner();

            // First tick to initialize
            env.World.Tick();
            env.World.LateTick();
            TestSystemLog.Clear();

            runner.IsPaused = true;
            env.World.Tick();
            // Don't call LateTick when paused — Tick returns early without
            // setting _variableDeltaTime, so LateTick would throw

            NAssert.AreEqual(
                0,
                TestSystemLog.ExecutionLog.Count,
                "No systems should execute when paused"
            );
        }

        #endregion
    }
}
