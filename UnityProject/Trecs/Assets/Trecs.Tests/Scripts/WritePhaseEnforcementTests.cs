using System;
using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    public partial struct WritePhaseSimComp : IEntityComponent
    {
        public int Value;
    }

    public partial struct WritePhaseRenderComp : IEntityComponent
    {
        public int Value;
    }

    public partial struct WritePhaseConstantComp : IEntityComponent
    {
        public int Value;
    }

    /// <summary>
    /// Component declared as <c>IsInput=true</c> on the test template — exists so
    /// <c>AddInput&lt;T&gt;</c> from the Input role has a real target component on
    /// the entity, mirroring how production input wiring looks.
    /// </summary>
    public partial struct WritePhaseInputComp : IEntityComponent
    {
        public int Value;
    }

    public struct WritePhaseSetTag : ITag { }

    public struct WritePhaseTestSet : IEntitySet<WritePhaseSetTag> { }

    /// <summary>
    /// Class payload for the <see cref="HeapAccessor.AllocSharedFrameScoped{T}(T)"/>
    /// positive-case test. Only needed because that overload constrains <c>T</c> to
    /// <c>class</c>; the body is empty.
    /// </summary>
    class WritePhaseHeapPayload { }

    [ExecuteIn(SystemPhase.Presentation)]
    partial class PresentationSimWriter : ISystem
    {
        public void Execute()
        {
            foreach (var idx in World.Query().WithTags(TestTags.Alpha).EntityIndices())
            {
                ref var v = ref World.Component<WritePhaseSimComp>(idx).Write;
                v.Value = 1;
            }
        }
    }

    [ExecuteIn(SystemPhase.Presentation)]
    partial class PresentationRenderWriter : ISystem
    {
        public void Execute()
        {
            foreach (var idx in World.Query().WithTags(TestTags.Alpha).EntityIndices())
            {
                ref var v = ref World.Component<WritePhaseRenderComp>(idx).Write;
                v.Value = 1;
            }
        }
    }

    partial class FixedSimWriter : ISystem
    {
        public void Execute()
        {
            foreach (var idx in World.Query().WithTags(TestTags.Alpha).EntityIndices())
            {
                ref var v = ref World.Component<WritePhaseSimComp>(idx).Write;
                v.Value = 1;
            }
        }
    }

    partial class FixedRenderWriter : ISystem
    {
        public void Execute()
        {
            foreach (var idx in World.Query().WithTags(TestTags.Alpha).EntityIndices())
            {
                ref var v = ref World.Component<WritePhaseRenderComp>(idx).Write;
                v.Value = 1;
            }
        }
    }

    partial class FixedRenderReader : ISystem
    {
        public void Execute()
        {
            foreach (var idx in World.Query().WithTags(TestTags.Alpha).EntityIndices())
            {
                ref readonly var _ = ref World.Component<WritePhaseRenderComp>(idx).Read;
            }
        }
    }

    [ExecuteIn(SystemPhase.Presentation)]
    partial class PresentationSimReader : ISystem
    {
        public void Execute()
        {
            foreach (var idx in World.Query().WithTags(TestTags.Alpha).EntityIndices())
            {
                ref readonly var _ = ref World.Component<WritePhaseSimComp>(idx).Read;
            }
        }
    }

    partial class FixedConstantWriter : ISystem
    {
        public void Execute()
        {
            foreach (var idx in World.Query().WithTags(TestTags.Alpha).EntityIndices())
            {
                ref var v = ref World.Component<WritePhaseConstantComp>(idx).Write;
                v.Value = 1;
            }
        }
    }

    partial class FixedConstantReader : ISystem
    {
        public int LastReadValue;

        public void Execute()
        {
            foreach (var idx in World.Query().WithTags(TestTags.Alpha).EntityIndices())
            {
                ref readonly var v = ref World.Component<WritePhaseConstantComp>(idx).Read;
                LastReadValue = v.Value;
            }
        }
    }

    [ExecuteIn(SystemPhase.Presentation)]
    partial class PresentationConstantReader : ISystem
    {
        public int LastReadValue;

        public void Execute()
        {
            foreach (var idx in World.Query().WithTags(TestTags.Alpha).EntityIndices())
            {
                ref readonly var v = ref World.Component<WritePhaseConstantComp>(idx).Read;
                LastReadValue = v.Value;
            }
        }
    }

    /// <summary>
    /// Fixed-phase <c>[WrapAsJob]</c> system that schedules a write job over a
    /// plain (sim) component. Exercises the
    /// <c>GetBufferWriteForJobScheduling</c> path's
    /// <c>AssertCanWriteComponent</c> for the legal case so the positive
    /// control matches the negative-case tests below.
    /// </summary>
    partial class FixedWrapAsJobSimWriter : ISystem
    {
        [ForEachEntity(Tag = typeof(WritePhaseSetTag))]
        [WrapAsJob]
        static void WriteSim(ref WritePhaseSimComp value)
        {
            value.Value = 1;
        }

        public void Execute()
        {
            WriteSim();
        }
    }

    /// <summary>
    /// Fixed-phase <c>[WrapAsJob]</c> system that tries to schedule a write
    /// job over a <c>[VariableUpdateOnly]</c> component. The job-scheduling
    /// path goes through <c>GetBufferWriteForJobScheduling</c>, which calls
    /// the same <c>AssertCanWriteComponent</c> as the main-thread path —
    /// this confirms the rule fires there too.
    /// </summary>
    partial class FixedWrapAsJobRenderWriter : ISystem
    {
        [ForEachEntity(Tag = typeof(WritePhaseSetTag))]
        [WrapAsJob]
        static void WriteRender(ref WritePhaseRenderComp value)
        {
            value.Value = 1;
        }

        public void Execute()
        {
            WriteRender();
        }
    }

    /// <summary>
    /// Fixed-phase <c>[WrapAsJob]</c> system that tries to schedule a read
    /// job over a <c>[VariableUpdateOnly]</c> component. Routes through
    /// <c>GetBufferReadForJobScheduling</c> →
    /// <c>AssertCanReadComponent</c>, mirroring the
    /// <c>FixedRenderReader</c> negative case for the main-thread path.
    /// </summary>
    partial class FixedWrapAsJobRenderReader : ISystem
    {
        [ForEachEntity(Tag = typeof(WritePhaseSetTag))]
        [WrapAsJob]
        static void ReadRender(in WritePhaseRenderComp value)
        {
            // Burst-visible use of `value` to keep the job from being
            // optimised away.
            _ = value.Value;
        }

        public void Execute()
        {
            ReadRender();
        }
    }

    [ExecuteIn(SystemPhase.Presentation)]
    partial class PresentationSetAdder : ISystem
    {
        public void Execute()
        {
            foreach (var idx in World.Query().WithTags(TestTags.Alpha).EntityIndices())
            {
                World.Set<WritePhaseTestSet>().Defer.Add(idx);
            }
        }
    }

    [ExecuteIn(SystemPhase.Presentation)]
    partial class PresentationSetRemover : ISystem
    {
        public void Execute()
        {
            foreach (var idx in World.Query().WithTags(TestTags.Alpha).EntityIndices())
            {
                World.Set<WritePhaseTestSet>().Defer.Remove(idx);
            }
        }
    }

    [ExecuteIn(SystemPhase.Presentation)]
    partial class PresentationSetWriter : ISystem
    {
        public void Execute()
        {
            // Forces SyncSetForWrite via the immediate SetAccessor.Write path
            // (a different entry point than Defer.Add / Defer.Remove).
            var _ = World.Set<WritePhaseTestSet>().Write;
        }
    }

    [ExecuteIn(SystemPhase.Presentation)]
    partial class PresentationBufferSimWriter : ISystem
    {
        public void Execute()
        {
            foreach (
                var group in World.WorldInfo.GetGroupsWithTagsAndComponents<WritePhaseSimComp>(
                    TagSet.FromTags(TestTags.Alpha)
                )
            )
            {
                var _ = World.ComponentBuffer<WritePhaseSimComp>(group).Write;
            }
        }
    }

    partial class FixedBufferRenderReader : ISystem
    {
        public void Execute()
        {
            foreach (
                var group in World.WorldInfo.GetGroupsWithTagsAndComponents<WritePhaseRenderComp>(
                    TagSet.FromTags(TestTags.Alpha)
                )
            )
            {
                var _ = World.ComponentBuffer<WritePhaseRenderComp>(group).Read;
            }
        }
    }

    /// <summary>
    /// Fixed-phase system that tries to read through an "editor"-style
    /// accessor (passed in via the test harness) instead of its own World
    /// accessor — exercises the strict
    /// "only the currently-executing system's accessor" rule.
    /// </summary>
    partial class FixedAlienAccessorReader : ISystem
    {
        public WorldAccessor AlienAccessor;

        public void Execute()
        {
            foreach (var idx in AlienAccessor.Query().WithTags(TestTags.Alpha).EntityIndices())
            {
                ref readonly var _ = ref AlienAccessor.Component<WritePhaseSimComp>(idx).Read;
            }
        }
    }

    [ExecuteIn(SystemPhase.Presentation)]
    partial class PresentationAlienAccessorReader : ISystem
    {
        public WorldAccessor AlienAccessor;

        public void Execute()
        {
            foreach (var idx in AlienAccessor.Query().WithTags(TestTags.Alpha).EntityIndices())
            {
                ref readonly var _ = ref AlienAccessor.Component<WritePhaseSimComp>(idx).Read;
            }
        }
    }

    // ── Input-phase systems ─────────────────────────────────────────
    //
    // Input role permissions exercised below:
    //   - AddInput<T>             → allowed (positive); from Fixed/Variable → rejected
    //   - Heap.Alloc*FrameScoped  → allowed
    //   - Heap.AllocShared        → rejected (must use FrameScoped variant)
    //   - Write [VariableUpdateOnly] component → allowed
    //   - Write non-VUO component → rejected
    //   - Read any component      → allowed
    //   - AddEntity / RemoveEntity / MoveTo / SetAdd → rejected
    //   - FixedRng                → rejected
    //
    // See AssertCanAddInputsSystem / AssertCanAllocatePersistent in
    // HeapAccessor and AssertCanWriteComponent / AssertCanMakeStructuralChanges
    // in WorldAccessor for the source-of-truth rules.

    [ExecuteIn(SystemPhase.Input)]
    partial class InputAddInputter : ISystem
    {
        public void Execute()
        {
            // Use the Alpha entity (known to exist post-CreateEnvWithSystem)
            // rather than World.GlobalEntityHandle — the GlobalEntityHandle
            // path resolves through EntityHandleMap and isn't populated for
            // the standalone Globals template that this test fixture builds.
            foreach (var idx in World.Query().WithTags(TestTags.Alpha).EntityIndices())
            {
                World.AddInput(idx, new WritePhaseInputComp { Value = 7 });
                return;
            }
        }
    }

    [ExecuteIn(SystemPhase.Input)]
    partial class InputAllocSharedFrameScopedSystem : ISystem
    {
        public void Execute()
        {
            var _ = World.Heap.AllocSharedFrameScoped<WritePhaseHeapPayload>(
                new WritePhaseHeapPayload()
            );
        }
    }

    [ExecuteIn(SystemPhase.Input)]
    partial class InputAllocSharedSystem : ISystem
    {
        public void Execute()
        {
            // Persistent (non-frame-scoped) allocation must be rejected from
            // the Input role with the "use the FrameScoped variant" message.
            var _ = World.Heap.AllocShared<WritePhaseHeapPayload>(new WritePhaseHeapPayload());
        }
    }

    [ExecuteIn(SystemPhase.Input)]
    partial class InputRenderWriter : ISystem
    {
        public void Execute()
        {
            foreach (var idx in World.Query().WithTags(TestTags.Alpha).EntityIndices())
            {
                ref var v = ref World.Component<WritePhaseRenderComp>(idx).Write;
                v.Value = 9;
            }
        }
    }

    [ExecuteIn(SystemPhase.Input)]
    partial class InputSimWriter : ISystem
    {
        public void Execute()
        {
            foreach (var idx in World.Query().WithTags(TestTags.Alpha).EntityIndices())
            {
                ref var v = ref World.Component<WritePhaseSimComp>(idx).Write;
                v.Value = 1;
            }
        }
    }

    [ExecuteIn(SystemPhase.Input)]
    partial class InputSimReader : ISystem
    {
        public int LastReadValue;

        public void Execute()
        {
            foreach (var idx in World.Query().WithTags(TestTags.Alpha).EntityIndices())
            {
                ref readonly var v = ref World.Component<WritePhaseSimComp>(idx).Read;
                LastReadValue = v.Value;
            }
        }
    }

    [ExecuteIn(SystemPhase.Input)]
    partial class InputRenderReader : ISystem
    {
        public int LastReadValue;

        public void Execute()
        {
            foreach (var idx in World.Query().WithTags(TestTags.Alpha).EntityIndices())
            {
                ref readonly var v = ref World.Component<WritePhaseRenderComp>(idx).Read;
                LastReadValue = v.Value;
            }
        }
    }

    [ExecuteIn(SystemPhase.Input)]
    partial class InputEntityAdder : ISystem
    {
        public void Execute()
        {
            World.AddEntity(TestTags.Alpha);
        }
    }

    [ExecuteIn(SystemPhase.Input)]
    partial class InputEntityRemover : ISystem
    {
        public void Execute()
        {
            // Pick any existing entity — the role check fires before the
            // entity-resolution path so we don't need a particular target.
            foreach (var idx in World.Query().WithTags(TestTags.Alpha).EntityIndices())
            {
                World.RemoveEntity(idx);
                return;
            }
        }
    }

    [ExecuteIn(SystemPhase.Input)]
    partial class InputMoveTo : ISystem
    {
        public void Execute()
        {
            foreach (var idx in World.Query().WithTags(TestTags.Alpha).EntityIndices())
            {
                // Target tag is irrelevant — AssertCanMakeStructuralChanges
                // throws before GetSingleGroupWithTags resolves the
                // destination group.
                World.MoveTo<WritePhaseSetTag>(idx);
                return;
            }
        }
    }

    [ExecuteIn(SystemPhase.Input)]
    partial class InputSetAdder : ISystem
    {
        public void Execute()
        {
            foreach (var idx in World.Query().WithTags(TestTags.Alpha).EntityIndices())
            {
                World.Set<WritePhaseTestSet>().Defer.Add(idx);
                return;
            }
        }
    }

    [ExecuteIn(SystemPhase.Input)]
    partial class InputFixedRngReader : ISystem
    {
        public void Execute()
        {
            // FixedRng routes through AssertCanMakeStructuralChanges, so
            // touching it from the Input role is rejected the same as any
            // other deterministic-state mutation.
            var _ = World.FixedRng;
        }
    }

    partial class FixedAddInputter : ISystem
    {
        public void Execute()
        {
            // See InputAddInputter for why this targets the Alpha entity
            // rather than GlobalEntityHandle.
            foreach (var idx in World.Query().WithTags(TestTags.Alpha).EntityIndices())
            {
                World.AddInput(idx, new WritePhaseInputComp { Value = 1 });
                return;
            }
        }
    }

    [ExecuteIn(SystemPhase.Presentation)]
    partial class PresentationAddInputter : ISystem
    {
        public void Execute()
        {
            foreach (var idx in World.Query().WithTags(TestTags.Alpha).EntityIndices())
            {
                World.AddInput(idx, new WritePhaseInputComp { Value = 2 });
                return;
            }
        }
    }

    [TestFixture]
    public class WritePhaseEnforcementTests
    {
        // Template carrying a plain (sim) component, a [VariableUpdateOnly]
        // (render) component, and a [Constant] component, so we can exercise
        // all three branches of the access rules against the same entity.
        static Template MakeTemplate()
        {
            return new Template(
                debugName: "WritePhaseTemplate",
                localBaseTemplates: Array.Empty<Template>(),
                partitions: Array.Empty<TagSet>(),
                localComponentDeclarations: new IComponentDeclaration[]
                {
                    new ComponentDeclaration<WritePhaseSimComp>(
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        default(WritePhaseSimComp)
                    ),
                    new ComponentDeclaration<WritePhaseRenderComp>(
                        variableUpdateOnly: true,
                        null,
                        null,
                        null,
                        null,
                        null,
                        default(WritePhaseRenderComp)
                    ),
                    new ComponentDeclaration<WritePhaseConstantComp>(
                        null,
                        null,
                        null,
                        null,
                        isConstant: true,
                        null,
                        default(WritePhaseConstantComp)
                    ),
                    new ComponentDeclaration<WritePhaseInputComp>(
                        null,
                        isInput: true,
                        inputFrameBehaviour: MissingInputBehavior.Retain,
                        null,
                        null,
                        null,
                        default(WritePhaseInputComp)
                    ),
                },
                localTags: new Tag[] { TestTags.Alpha, Tag<WritePhaseSetTag>.Value }
            );
        }

        TestEnvironment CreateEnvWithSystem(ISystem system)
        {
            var builder = new WorldBuilder()
                .SetSettings(new WorldSettings())
                .AddTemplate(TrecsTemplates.Globals.Template)
                .AddTemplate(MakeTemplate())
                .AddSet<WritePhaseTestSet>()
                .AddBlobStore(EcsTestHelper.CreateBlobStore());

            var world = builder.Build();
            world.AddSystem(system);
            world.Initialize();

            var env = new TestEnvironment(world);
            env.Accessor.AddEntity(TestTags.Alpha).AssertComplete();
            env.Accessor.SubmitEntities();
            return env;
        }

        [Test]
        public void Write_FromPresentation_ToNonVariableUpdateOnly_Throws()
        {
            using var env = CreateEnvWithSystem(new PresentationSimWriter());
            NAssert.Throws<TrecsException>(() => env.World.Tick());
        }

        [Test]
        public void Write_FromPresentation_ToVariableUpdateOnly_Succeeds()
        {
            using var env = CreateEnvWithSystem(new PresentationRenderWriter());
            NAssert.DoesNotThrow(() => env.World.Tick());

            var comp = env
                .Accessor.Query()
                .WithTags(TestTags.Alpha)
                .Single()
                .Get<WritePhaseRenderComp>();
            NAssert.AreEqual(1, comp.Read.Value);
        }

        [Test]
        public void Write_FromFixed_ToNonVariableUpdateOnly_Succeeds()
        {
            using var env = CreateEnvWithSystem(new FixedSimWriter());
            NAssert.DoesNotThrow(() => env.World.Tick());

            var comp = env
                .Accessor.Query()
                .WithTags(TestTags.Alpha)
                .Single()
                .Get<WritePhaseSimComp>();
            NAssert.AreEqual(1, comp.Read.Value);
        }

        [Test]
        public void Write_FromFixed_ToVariableUpdateOnly_Throws()
        {
            using var env = CreateEnvWithSystem(new FixedRenderWriter());
            NAssert.Throws<TrecsException>(() => env.World.Tick());
        }

        [Test]
        public void Read_FromFixed_OfVariableUpdateOnly_Throws()
        {
            using var env = CreateEnvWithSystem(new FixedRenderReader());
            NAssert.Throws<TrecsException>(() => env.World.Tick());
        }

        [Test]
        public void Read_FromPresentation_OfNonVariableUpdateOnly_Succeeds()
        {
            using var env = CreateEnvWithSystem(new PresentationSimReader());
            NAssert.DoesNotThrow(() => env.World.Tick());
        }

        [Test]
        public void Write_ToConstantComponent_ThroughBufferAccess_Throws()
        {
            using var env = CreateEnvWithSystem(new FixedConstantWriter());
            NAssert.Throws<TrecsException>(() => env.World.Tick());
        }

        [Test]
        public void Read_FromFixed_OfConstantComponent_Succeeds()
        {
            // [Constant] components may be read from any phase — only writes
            // are rejected post-creation. Asserts the Fixed read path does
            // not trip the AssertCanReadComponent guard.
            var system = new FixedConstantReader();
            using var env = CreateEnvWithSystem(system);
            NAssert.DoesNotThrow(() => env.World.Tick());
            // Default value of WritePhaseConstantComp.Value is 0; just
            // confirms the read landed (system mutated its public field).
            NAssert.AreEqual(0, system.LastReadValue);
        }

        [Test]
        public void Read_FromPresentation_OfConstantComponent_Succeeds()
        {
            // Variable-cadence phases skip the read guard entirely (it
            // early-returns when !IsFixed), but having a Presentation read
            // case alongside the Fixed one documents both paths.
            var system = new PresentationConstantReader();
            using var env = CreateEnvWithSystem(system);
            NAssert.DoesNotThrow(() => env.World.Tick());
            NAssert.AreEqual(0, system.LastReadValue);
        }

        [Test]
        public void WrapAsJob_Write_FromFixed_ToNonVariableUpdateOnly_Succeeds()
        {
            // Positive control for the [WrapAsJob] scheduling path: a
            // sim-component write should clear AssertCanWriteComponent and
            // reach Burst job execution without throwing.
            using var env = CreateEnvWithSystem(new FixedWrapAsJobSimWriter());
            NAssert.DoesNotThrow(() => env.World.Tick());

            var comp = env
                .Accessor.Query()
                .WithTags(TestTags.Alpha)
                .Single()
                .Get<WritePhaseSimComp>();
            NAssert.AreEqual(1, comp.Read.Value);
        }

        [Test]
        public void WrapAsJob_Write_FromFixed_ToVariableUpdateOnly_Throws()
        {
            // [WrapAsJob] routes through GetBufferWriteForJobScheduling,
            // which calls the same AssertCanWriteComponent as the
            // main-thread GetBufferWrite path. A Fixed-phase write to a
            // [VariableUpdateOnly] component must therefore throw on the
            // job-scheduling side too.
            using var env = CreateEnvWithSystem(new FixedWrapAsJobRenderWriter());
            NAssert.Throws<TrecsException>(() => env.World.Tick());
        }

        [Test]
        public void WrapAsJob_Read_FromFixed_OfVariableUpdateOnly_Throws()
        {
            // Symmetrical case for the read path:
            // GetBufferReadForJobScheduling → AssertCanReadComponent must
            // reject reading a [VariableUpdateOnly] component from Fixed
            // even when the access is scheduled as a job.
            using var env = CreateEnvWithSystem(new FixedWrapAsJobRenderReader());
            NAssert.Throws<TrecsException>(() => env.World.Tick());
        }

        [Test]
        public void SetAdd_FromPresentation_Throws()
        {
            using var env = CreateEnvWithSystem(new PresentationSetAdder());
            NAssert.Throws<TrecsException>(() => env.World.Tick());
        }

        [Test]
        public void SetRemove_FromPresentation_Throws()
        {
            using var env = CreateEnvWithSystem(new PresentationSetRemover());
            NAssert.Throws<TrecsException>(() => env.World.Tick());
        }

        [Test]
        public void SetAccessorWrite_FromPresentation_Throws()
        {
            using var env = CreateEnvWithSystem(new PresentationSetWriter());
            NAssert.Throws<TrecsException>(() => env.World.Tick());
        }

        [Test]
        public void BufferWrite_FromPresentation_ToNonVariableUpdateOnly_Throws()
        {
            using var env = CreateEnvWithSystem(new PresentationBufferSimWriter());
            NAssert.Throws<TrecsException>(() => env.World.Tick());
        }

        [Test]
        public void BufferRead_FromFixed_OfVariableUpdateOnly_Throws()
        {
            using var env = CreateEnvWithSystem(new FixedBufferRenderReader());
            NAssert.Throws<TrecsException>(() => env.World.Tick());
        }

        [Test]
        public void AlienAccessor_DuringFixedExecute_Throws()
        {
            // The "alien" accessor is a Bypass accessor — exactly the
            // service-class pattern the strict rule is meant to catch (an
            // accessor whose Id doesn't match the executing system's).
            var system = new FixedAlienAccessorReader();
            using var env = CreateEnvWithSystem(system);
            system.AlienAccessor = env.World.CreateAccessor(
                AccessorRole.Unrestricted,
                "ServiceLikeAccessor"
            );
            NAssert.Throws<TrecsException>(() => env.World.Tick());
        }

        [Test]
        public void AlienAccessor_DuringFixedExecute_FixedRole_Throws()
        {
            // The strict rule keys on accessor identity, not on role —
            // a Fixed-role service holding its own accessor (matching
            // role, just a different Id) is rejected the same as a
            // Bypass accessor would be.
            var system = new FixedAlienAccessorReader();
            using var env = CreateEnvWithSystem(system);
            system.AlienAccessor = env.World.CreateAccessor(
                AccessorRole.Fixed,
                "FixedServiceAccessor"
            );
            NAssert.Throws<TrecsException>(() => env.World.Tick());
        }

        [Test]
        public void AlienAccessor_DuringPresentationExecute_Succeeds()
        {
            // Variable-cadence phases don't enforce the rule — Bypass
            // accessors and other-system accessors may freely touch state
            // there because none of those phases have determinism
            // guarantees the rule could break.
            var system = new PresentationAlienAccessorReader();
            using var env = CreateEnvWithSystem(system);
            system.AlienAccessor = env.World.CreateAccessor(
                AccessorRole.Unrestricted,
                "ServiceLikeAccessor"
            );
            NAssert.DoesNotThrow(() => env.World.Tick());
        }

        [Test]
        public void AlienAccessor_OutsideExecute_Succeeds()
        {
            // The rule only fires while a Fixed system's Execute is running.
            // Reads from a standalone accessor between ticks are normal.
            using var env = CreateEnvWithSystem(new FixedSimWriter());
            env.World.Tick(); // Tick once so SimWriter sets the value to 1.

            var alien = env.World.CreateAccessor(AccessorRole.Unrestricted, "ServiceLikeAccessor");
            NAssert.DoesNotThrow(() =>
            {
                foreach (var idx in alien.Query().WithTags(TestTags.Alpha).EntityIndices())
                {
                    ref readonly var _ = ref alien.Component<WritePhaseSimComp>(idx).Read;
                }
            });
        }

        // ── Input-role tests ────────────────────────────────────────
        //
        // None is intentionally allowed by AssertCanAddInputsSystem
        // (`IsUnrestricted || IsInput`) — it's the documented escape hatch — so
        // we don't have a "None rejected" case here. Negative coverage
        // sits on Fixed and Variable instead, which is where production
        // misuse would actually originate.

        [Test]
        public void AddInput_FromInput_Succeeds()
        {
            using var env = CreateEnvWithSystem(new InputAddInputter());
            NAssert.DoesNotThrow(() => env.World.Tick());
        }

        [Test]
        public void AddInput_FromFixed_Throws()
        {
            using var env = CreateEnvWithSystem(new FixedAddInputter());
            NAssert.Throws<TrecsException>(() => env.World.Tick());
        }

        [Test]
        public void AddInput_FromPresentation_Throws()
        {
            // Variable-role coverage for AssertCanAddInputsSystem — paired
            // with the Fixed case above to lock in that only Input/Bypass
            // can enqueue inputs.
            using var env = CreateEnvWithSystem(new PresentationAddInputter());
            NAssert.Throws<TrecsException>(() => env.World.Tick());
        }

        [Test]
        public void AllocSharedFrameScoped_FromInput_Succeeds()
        {
            // Stand-in for all AllocXxxFrameScoped overloads — they share
            // the AssertCanAddInputsSystem gate, so one positive test is
            // enough to lock in that Input clears it.
            using var env = CreateEnvWithSystem(new InputAllocSharedFrameScopedSystem());
            NAssert.DoesNotThrow(() => env.World.Tick());
        }

        [Test]
        public void AllocShared_FromInput_Throws()
        {
            // Persistent-heap allocation is the most-used Alloc overload to
            // gate; AssertCanAllocatePersistent has a dedicated Input branch
            // that points at the FrameScoped variant in its message.
            using var env = CreateEnvWithSystem(new InputAllocSharedSystem());
            NAssert.Throws<TrecsException>(() => env.World.Tick());
        }

        [Test]
        public void Write_FromInput_ToVariableUpdateOnly_Succeeds()
        {
            // [VariableUpdateOnly] is render-rate state. Input role is
            // explicitly listed as one of the allowed writers (Input /
            // Variable / Bypass) so the AssertCanWriteComponent guard must
            // pass here.
            using var env = CreateEnvWithSystem(new InputRenderWriter());
            NAssert.DoesNotThrow(() => env.World.Tick());
        }

        [Test]
        public void Write_FromInput_ToNonVariableUpdateOnly_Throws()
        {
            // Sim components must only be written from Fixed (or Bypass) —
            // an Input write trips the "must be declared
            // [VariableUpdateOnly]" branch of AssertCanWriteComponent.
            using var env = CreateEnvWithSystem(new InputSimWriter());
            NAssert.Throws<TrecsException>(() => env.World.Tick());
        }

        [Test]
        public void Read_FromInput_OfNonVariableUpdateOnly_Succeeds()
        {
            // AssertCanReadComponent early-returns for !IsFixed; Input is
            // not Fixed, so reading any sim component is allowed.
            var system = new InputSimReader();
            using var env = CreateEnvWithSystem(system);
            NAssert.DoesNotThrow(() => env.World.Tick());
            NAssert.AreEqual(0, system.LastReadValue);
        }

        [Test]
        public void Read_FromInput_OfVariableUpdateOnly_Succeeds()
        {
            // The read guard is Fixed-only — VUO components are freely
            // readable from Input/Variable/Bypass roles.
            var system = new InputRenderReader();
            using var env = CreateEnvWithSystem(system);
            NAssert.DoesNotThrow(() => env.World.Tick());
            NAssert.AreEqual(0, system.LastReadValue);
        }

        [Test]
        public void AddEntity_FromInput_Throws()
        {
            // AddEntity routes through AssertCanMakeStructuralChanges
            // (CanMakeStructuralChanges = IsUnrestricted || IsFixed), so the
            // input system role is rejected.
            using var env = CreateEnvWithSystem(new InputEntityAdder());
            NAssert.Throws<TrecsException>(() => env.World.Tick());
        }

        [Test]
        public void RemoveEntity_FromInput_Throws()
        {
            using var env = CreateEnvWithSystem(new InputEntityRemover());
            NAssert.Throws<TrecsException>(() => env.World.Tick());
        }

        [Test]
        public void MoveTo_FromInput_Throws()
        {
            using var env = CreateEnvWithSystem(new InputMoveTo());
            NAssert.Throws<TrecsException>(() => env.World.Tick());
        }

        [Test]
        public void SetAdd_FromInput_Throws()
        {
            using var env = CreateEnvWithSystem(new InputSetAdder());
            NAssert.Throws<TrecsException>(() => env.World.Tick());
        }

        [Test]
        public void FixedRng_FromInput_Throws()
        {
            // FixedRng's getter calls AssertCanMakeStructuralChanges — the
            // simulation RNG stream is part of deterministic state, so only
            // Fixed/Bypass may pull values. Touching it from Input would
            // desync replay across runs.
            using var env = CreateEnvWithSystem(new InputFixedRngReader());
            NAssert.Throws<TrecsException>(() => env.World.Tick());
        }
    }
}
