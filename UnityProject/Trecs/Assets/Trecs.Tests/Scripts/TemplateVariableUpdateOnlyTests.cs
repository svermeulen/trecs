using System;
using System.IO;
using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    // Component carried by the VUO template under test. No per-field
    // [VariableUpdateOnly] declaration — the rule must come from the
    // template-level VUO flag alone, otherwise these tests don't actually
    // exercise the new path (they would fall through to the existing
    // per-component rule).
    public partial struct VuoTemplateRenderComp : IEntityComponent
    {
        public int Value;
    }

    // Tag exclusive to the VUO template's group.
    public struct VuoTemplateTag : ITag { }

    // Presence/absence partition variant on the VUO template — used so the
    // SetTag-based mover test below has a partition variant to flip. Without
    // a partition the VUO template has no dim and SetTag has nothing to do.
    public struct VuoPartitionTag : ITag { }

    // Tag exclusive to the child-of-VUO-base template's group, used by the
    // inheritance test below. Distinct from VuoTemplateTag so the two
    // templates don't share groups.
    public struct VuoChildTag : ITag { }

    // Tag carried by the VUO base template; the child inherits this tag
    // through Template.LocalBaseTemplates as well as the VUO flag. Required
    // because every template must have at least one local tag.
    public struct VuoBaseTag : ITag { }

    [ExecuteIn(SystemPhase.Presentation)]
    partial class VuoVariableEntityAdder : ISystem
    {
        public void Execute()
        {
            World.AddEntity<VuoTemplateTag>().AssertComplete();
        }
    }

    partial class VuoFixedEntityAdder : ISystem
    {
        public void Execute()
        {
            // Fixed-role accessor adding to a VUO template — must throw.
            World.AddEntity<VuoTemplateTag>().AssertComplete();
        }
    }

    [ExecuteIn(SystemPhase.Presentation)]
    partial class VuoVariableEntityRemover : ISystem
    {
        public void Execute()
        {
            foreach (var idx in World.Query().WithTags<VuoTemplateTag>().Indices())
            {
                World.RemoveEntity(idx);
                return;
            }
        }
    }

    partial class VuoFixedQueryReader : ISystem
    {
        public void Execute()
        {
            // Querying a tag that resolves to a VUO group from Fixed must
            // throw at query construction time per the design — silent
            // filtering hides the underlying mistake.
            foreach (var _ in World.Query().WithTags<VuoTemplateTag>().Indices())
            {
                // unreachable
            }
        }
    }

    // Used by RemoveEntitiesWithTags coverage — Fixed-role bulk remove
    // against a VUO tag must throw (each group inside the loop is checked).
    partial class VuoFixedBulkRemover : ISystem
    {
        public void Execute()
        {
            World.RemoveEntitiesWithTags(TagSet<VuoTemplateTag>.Value);
        }
    }

    // Used by MoveTo coverage — Fixed-role moves originating from a VUO
    // group must throw (the source-group check fires before the dest is
    // even computed). The target index is set after world initialization
    // so the test can populate the VUO group via Bypass first.
    partial class VuoFixedMover : ISystem
    {
        public EntityIndex Victim;

        public void Execute()
        {
            World.SetTag<VuoPartitionTag>(Victim);
        }
    }

    // Used by inheritance coverage — a Fixed-role query against a child
    // template that inherits VUO from its base must be rejected, exactly
    // as if the child were directly declared VUO.
    partial class VuoFixedChildQueryReader : ISystem
    {
        public void Execute()
        {
            foreach (var _ in World.Query().WithTags<VuoChildTag>().Indices())
            {
                // unreachable — query construction should throw.
            }
        }
    }

    [TestFixture]
    public class TemplateVariableUpdateOnlyTests
    {
        // Template carrying a single ordinary component but declared
        // [VariableUpdateOnly] at the template level via its constructor.
        // The component declaration is intentionally NOT marked VUO — we
        // want to verify the template-level flag propagates the rule
        // through AssertCan{Read,Write}Component without per-field opt-in.
        static Template MakeVuoTemplate()
        {
            return new Template(
                debugName: "VuoTestTemplate",
                localBaseTemplates: Array.Empty<Template>(),
                partitions: new TagSet[]
                {
                    TagSet.Null,
                    TagSet.FromTags(Tag<VuoPartitionTag>.Value),
                },
                localComponentDeclarations: new IComponentDeclaration[]
                {
                    new ComponentDeclaration<VuoTemplateRenderComp>(
                        null,
                        null,
                        null,
                        null,
                        null,
                        default(VuoTemplateRenderComp)
                    ),
                },
                localTags: new Tag[] { Tag<VuoTemplateTag>.Value },
                localVariableUpdateOnly: true,
                dimensions: new TagSet[] { TagSet.FromTags(Tag<VuoPartitionTag>.Value) }
            );
        }

        // Base template marked [VariableUpdateOnly]; carries one tag so it
        // satisfies the "every template has at least one tag" constraint.
        // Used to verify that VUO inherits transitively through
        // Template.LocalBaseTemplates via WorldInfo.ResolveTemplate.
        static Template MakeVuoBaseTemplate()
        {
            return new Template(
                debugName: "VuoTestBase",
                localBaseTemplates: Array.Empty<Template>(),
                partitions: Array.Empty<TagSet>(),
                localComponentDeclarations: Array.Empty<IComponentDeclaration>(),
                localTags: new Tag[] { Tag<VuoBaseTag>.Value },
                localVariableUpdateOnly: true
            );
        }

        // Child template that inherits VUO from the base above. The child
        // itself does NOT declare [VariableUpdateOnly] — the VUO rule should
        // propagate via inheritance only.
        static Template MakeChildOfVuoBase(Template baseTemplate)
        {
            return new Template(
                debugName: "VuoTestChild",
                localBaseTemplates: new[] { baseTemplate },
                partitions: Array.Empty<TagSet>(),
                localComponentDeclarations: new IComponentDeclaration[]
                {
                    new ComponentDeclaration<VuoTemplateRenderComp>(
                        null,
                        null,
                        null,
                        null,
                        null,
                        default(VuoTemplateRenderComp)
                    ),
                },
                localTags: new Tag[] { Tag<VuoChildTag>.Value },
                localVariableUpdateOnly: false
            );
        }

        TestEnvironment CreateEnvWithSystem(ISystem system)
        {
            var builder = new WorldBuilder()
                .SetSettings(new WorldSettings())
                .AddTemplate(TrecsTemplates.Globals.Template)
                .AddTemplate(MakeVuoTemplate())
                .AddBlobStore(EcsTestHelper.CreateBlobStore());

            var world = builder.Build();
            world.AddSystem(system);
            world.Initialize();

            // Pre-populate one VUO entity via a Bypass accessor so reads
            // and queries have something to chew on. Bypass is the
            // documented setup hatch.
            var env = new TestEnvironment(world);
            env.Accessor.AddEntity<VuoTemplateTag>().AssertComplete();
            env.Accessor.SubmitEntities();
            return env;
        }

        [Test]
        public void AddEntity_FromVariable_OnVuoTemplate_Succeeds()
        {
            using var env = CreateEnvWithSystem(new VuoVariableEntityAdder());
            NAssert.DoesNotThrow(() => env.World.Tick());
        }

        [Test]
        public void AddEntity_FromFixed_OnVuoTemplate_Throws()
        {
            using var env = CreateEnvWithSystem(new VuoFixedEntityAdder());
            NAssert.Throws<TrecsException>(() => env.World.Tick());
        }

        [Test]
        public void RemoveEntity_FromVariable_OnVuoTemplate_Succeeds()
        {
            // Variable-cadence systems are allowed to remove entities from
            // VUO templates — that's the entire point of template-level VUO.
            using var env = CreateEnvWithSystem(new VuoVariableEntityRemover());
            NAssert.DoesNotThrow(() => env.World.Tick());
        }

        [Test]
        public void Query_FromFixed_OnVuoTemplate_Throws()
        {
            // The query construction check fires before any per-entity
            // access — Fixed-role iteration over a VUO group is rejected
            // outright instead of being silently filtered to empty.
            using var env = CreateEnvWithSystem(new VuoFixedQueryReader());
            NAssert.Throws<TrecsException>(() => env.World.Tick());
        }

        // Observer registration that resolves to a VUO template's groups
        // must be rejected at registration time when the registering
        // accessor is Fixed-role. The Variable case is the positive
        // control: a Variable-role accessor subscribes successfully.
        // Both cases register from Initialize-time service accessors
        // (the documented entry point for cross-system event hookups
        // per RemoveCleanupHandler), not from inside a system's Execute.
        static World BuildWorldForObserverTests()
        {
            var builder = new WorldBuilder()
                .SetSettings(new WorldSettings())
                .AddTemplate(TrecsTemplates.Globals.Template)
                .AddTemplate(MakeVuoTemplate())
                .AddBlobStore(EcsTestHelper.CreateBlobStore());

            return builder.BuildAndInitialize();
        }

        [Test]
        public void Observer_OnRemoved_FromVariable_OnVuoTemplate_Succeeds()
        {
            using var env = new TestEnvironment(BuildWorldForObserverTests());
            var variableAccessor = env.World.CreateAccessor(
                AccessorRole.Variable,
                debugName: "VariableObserverService"
            );

            NAssert.DoesNotThrow(() =>
            {
                using var sub = variableAccessor
                    .Events.EntitiesWithTags<VuoTemplateTag>()
                    .OnRemoved((GroupIndex group, EntityRange indices) => { });
            });
        }

        [Test]
        public void Observer_OnRemoved_FromFixed_OnVuoTemplate_Throws()
        {
            // A Fixed-role service that subscribes to lifecycle events on
            // a [VariableUpdateOnly] template's groups can never see a
            // valid callback (structural changes on VUO groups are driven
            // by Variable / Input / Bypass roles, not Fixed). Reject at
            // registration so the mistake surfaces at startup, not at the
            // first never-fires callback.
            using var env = new TestEnvironment(BuildWorldForObserverTests());
            var fixedAccessor = env.World.CreateAccessor(
                AccessorRole.Fixed,
                debugName: "FixedObserverService"
            );

            NAssert.Throws<TrecsException>(() =>
            {
                fixedAccessor
                    .Events.EntitiesWithTags<VuoTemplateTag>()
                    .OnRemoved((GroupIndex group, EntityRange indices) => { });
            });
        }

        [Test]
        public void Observer_OnAdded_FromFixed_OnVuoTemplate_Throws()
        {
            // OnAdded mirrors OnRemoved: same registration-time guard.
            using var env = new TestEnvironment(BuildWorldForObserverTests());
            var fixedAccessor = env.World.CreateAccessor(
                AccessorRole.Fixed,
                debugName: "FixedObserverService"
            );

            NAssert.Throws<TrecsException>(() =>
            {
                fixedAccessor
                    .Events.EntitiesWithTags<VuoTemplateTag>()
                    .OnAdded((GroupIndex group, EntityRange indices) => { });
            });
        }

        [Test]
        public void Observer_OnMoved_FromFixed_OnVuoTemplate_Throws()
        {
            // OnMoved mirrors OnRemoved: same registration-time guard.
            using var env = new TestEnvironment(BuildWorldForObserverTests());
            var fixedAccessor = env.World.CreateAccessor(
                AccessorRole.Fixed,
                debugName: "FixedObserverService"
            );

            NAssert.Throws<TrecsException>(() =>
            {
                fixedAccessor
                    .Events.EntitiesWithTags<VuoTemplateTag>()
                    .OnMoved((GroupIndex from, GroupIndex to, EntityRange indices) => { });
            });
        }

        [Test]
        public void RemoveEntitiesWithTags_FromFixed_OnVuoTemplate_Throws()
        {
            // RemoveEntitiesWithTags resolves to one or more groups and
            // checks each one inside the loop. The Fixed-role per-group
            // check on a VUO template must fire on the first iteration.
            using var env = CreateEnvWithSystem(new VuoFixedBulkRemover());
            NAssert.Throws<TrecsException>(() => env.World.Tick());
        }

        [Test]
        public void MoveTo_FromFixed_FromVuoTemplate_Throws()
        {
            // MoveTo runs the per-group check against the source group
            // first; for an entity already on a VUO template, the source
            // check rejects Fixed-role before the destination is even
            // computed.
            var mover = new VuoFixedMover();
            using var env = CreateEnvWithSystem(mover);

            // CreateEnvWithSystem pre-populates one VUO entity via Bypass.
            // Bind the mover to it before the first Tick so Execute has a
            // valid victim index to feed MoveTo.
            mover.Victim = env.Accessor.Query().WithTags<VuoTemplateTag>().SingleIndex();

            NAssert.Throws<TrecsException>(() => env.World.Tick());
        }

        [Test]
        public void Query_FromFixed_OnTemplateInheritingVuo_Throws()
        {
            // VUO inherits transitively through Template.LocalBaseTemplates.
            // A child template with localVariableUpdateOnly: false but with
            // a VUO base must still resolve to VariableUpdateOnly = true
            // and reject Fixed-role queries.
            var baseTemplate = MakeVuoBaseTemplate();
            var childTemplate = MakeChildOfVuoBase(baseTemplate);

            // Only the concrete (child) template is registered — the base
            // is pulled in via LocalBaseTemplates. Registering both is
            // explicitly rejected with "Found X as a base type of Y".
            var builder = new WorldBuilder()
                .SetSettings(new WorldSettings())
                .AddTemplate(TrecsTemplates.Globals.Template)
                .AddTemplate(childTemplate)
                .AddBlobStore(EcsTestHelper.CreateBlobStore());

            var world = builder.Build();
            world.AddSystem(new VuoFixedChildQueryReader());
            world.Initialize();

            using var env = new TestEnvironment(world);
            NAssert.Throws<TrecsException>(() => env.World.Tick());
        }

        [Test]
        public void SnapshotRoundTrip_PreservesVuoTemplateState()
        {
            // Determinism: a snapshot write (no IsForChecksum flag) must
            // include VUO template component arrays so save/restore
            // reproduces the render-side state exactly. Checksum-write
            // (IsForChecksum=true) is what skips them; this test exercises
            // the snapshot path explicitly.
            var builder = new WorldBuilder()
                .SetSettings(new WorldSettings())
                .AddTemplate(TrecsTemplates.Globals.Template)
                .AddTemplate(MakeVuoTemplate())
                .AddBlobStore(EcsTestHelper.CreateBlobStore());

            using var env = new TestEnvironment(builder.BuildAndInitialize());

            // Populate the VUO template and write a real component value
            // (Bypass writes are allowed everywhere).
            env.Accessor.AddEntity<VuoTemplateTag>()
                .Set(new VuoTemplateRenderComp { Value = 7 })
                .AssertComplete();
            env.Accessor.SubmitEntities();

            var entityIdx = env.Accessor.Query().WithTags<VuoTemplateTag>().SingleIndex();

            // Round-trip through the snapshot serializer. Mirrors the
            // pattern in WorldStateSerializerTests — write to a memory
            // buffer, mutate state, then read back and confirm restoration
            // overwrote the mutation.
            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var serializer = new WorldStateSerializer(env.World);
            byte[] snapshotBytes;
            using (var writer = new BinarySerializationWriter(registry))
            {
                writer.Start(version: 1, includeTypeChecks: false);
                serializer.SerializeState(writer);

                using var outputStream = new MemoryStream();
                using var outputWriter = new BinaryWriter(outputStream);
                writer.Complete(outputWriter);
                snapshotBytes = outputStream.ToArray();
            }

            // Mutate state to confirm restore actually overwrites.
            env.Accessor.Component<VuoTemplateRenderComp>(entityIdx).Write.Value = 99;

            using (var inputStream = new MemoryStream(snapshotBytes))
            using (var inputReader = new BinaryReader(inputStream))
            {
                var reader = new BinarySerializationReader(registry);
                reader.Start(inputReader);
                serializer.DeserializeState(reader);
            }

            var restoredIdx = env.Accessor.Query().WithTags<VuoTemplateTag>().SingleIndex();
            NAssert.AreEqual(
                7,
                env.Accessor.Component<VuoTemplateRenderComp>(restoredIdx).Read.Value,
                "VUO template component value must round-trip through snapshot/restore"
            );
        }

        [Test]
        public void DeserializeState_FromChecksumStream_Throws()
        {
            // Checksum-mode streams skip VUO template component arrays at
            // write time (ShouldSkip). Restoring from one would silently
            // zero render-side state, so we treat checksum streams as
            // caller error at restore time.
            var builder = new WorldBuilder()
                .SetSettings(new WorldSettings())
                .AddTemplate(TrecsTemplates.Globals.Template)
                .AddTemplate(MakeVuoTemplate())
                .AddBlobStore(EcsTestHelper.CreateBlobStore());

            using var env = new TestEnvironment(builder.BuildAndInitialize());

            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var serializer = new WorldStateSerializer(env.World);

            byte[] checksumBytes;
            using (var writer = new BinarySerializationWriter(registry))
            {
                writer.Start(
                    version: 1,
                    includeTypeChecks: false,
                    flags: SerializationFlags.IsForChecksum
                );
                serializer.SerializeState(writer);

                using var outputStream = new MemoryStream();
                using var outputWriter = new BinaryWriter(outputStream);
                writer.Complete(outputWriter);
                checksumBytes = outputStream.ToArray();
            }

            NAssert.Throws<TrecsException>(() =>
            {
                using var inputStream = new MemoryStream(checksumBytes);
                using var inputReader = new BinaryReader(inputStream);
                var reader = new BinarySerializationReader(registry);
                reader.Start(inputReader);
                serializer.DeserializeState(reader);
            });
        }

        [Test]
        public void ChecksumStream_OmitsVuoComponentBytes_ComparedToSnapshot()
        {
            // Sanity check that ShouldSkip actually fires for IsForChecksum
            // writes — a checksum-mode write of a populated VUO template
            // must produce a smaller byte stream than a snapshot-mode
            // write of the same world state. If the IsForChecksum branch
            // ever inverts, this test catches it.
            var builder = new WorldBuilder()
                .SetSettings(new WorldSettings())
                .AddTemplate(TrecsTemplates.Globals.Template)
                .AddTemplate(MakeVuoTemplate())
                .AddBlobStore(EcsTestHelper.CreateBlobStore());

            using var env = new TestEnvironment(builder.BuildAndInitialize());

            // Populate enough VUO entities that the omitted bytes are
            // measurable beyond stream framing overhead.
            for (int i = 0; i < 16; i++)
            {
                env.Accessor.AddEntity<VuoTemplateTag>()
                    .Set(new VuoTemplateRenderComp { Value = i })
                    .AssertComplete();
            }
            env.Accessor.SubmitEntities();

            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            var serializer = new WorldStateSerializer(env.World);

            int snapshotLength = WriteAndMeasure(serializer, registry, flags: 0);
            int checksumLength = WriteAndMeasure(
                serializer,
                registry,
                flags: SerializationFlags.IsForChecksum
            );

            NAssert.Less(
                checksumLength,
                snapshotLength,
                "Checksum-mode write should omit VUO template component bytes; "
                    + "checksumLength={0}, snapshotLength={1}",
                checksumLength,
                snapshotLength
            );
        }

        static int WriteAndMeasure(
            WorldStateSerializer serializer,
            SerializerRegistry registry,
            long flags
        )
        {
            using var writer = new BinarySerializationWriter(registry);
            writer.Start(version: 1, includeTypeChecks: false, flags: flags);
            serializer.SerializeState(writer);

            using var outputStream = new MemoryStream();
            using var outputWriter = new BinaryWriter(outputStream);
            writer.Complete(outputWriter);
            return (int)outputStream.Length;
        }
    }
}
