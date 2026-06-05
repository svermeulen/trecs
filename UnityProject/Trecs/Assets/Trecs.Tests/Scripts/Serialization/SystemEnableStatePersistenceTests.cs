using System;
using System.IO;
using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    partial class SerEnableSystemA : ISystem
    {
        public void Execute() { }
    }

    partial class SerEnableSystemB : ISystem
    {
        public void Execute() { }
    }

    /// <summary>
    /// Verifies that paused state participates in world serialization (deterministic
    /// across snapshots), while channel state does not (ephemeral, app-side).
    /// </summary>
    [TestFixture]
    public class SystemEnableStatePersistenceTests
    {
        SerializerRegistry _serializerRegistry;

        [SetUp]
        public void SetUp()
        {
            _serializerRegistry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(_serializerRegistry);
        }

        World CreateWorld()
        {
            var globals = TestTemplate
                .Named("TestGlobals")
                .Extending(TrecsTemplates.Globals.Template)
                .Build();

            return new WorldBuilder()
                .SetSettings(new WorldSettings())
                .AddTemplate(globals)
                .AddSystem(new SerEnableSystemA())
                .AddSystem(new SerEnableSystemB())
                .BuildAndInitialize();
        }

        byte[] SerializeWorld(World world)
        {
            var serializer = new WorldStateSerializer(world);
            var writer = new BinarySerializationWriter(_serializerRegistry);
            var serData = new SerializationData();
            writer.Start(serData, version: 1, includeTypeChecks: false);
            serializer.SerializeFullState(writer);

            using var outputStream = new MemoryStream();
            writer.Complete();
            serData.WriteContiguousTo(outputStream);
            return outputStream.ToArray();
        }

        void DeserializeWorld(World world, byte[] data)
        {
            var serializer = new WorldStateSerializer(world);
            var reader = new BinarySerializationReader(_serializerRegistry);
            reader.Start(new ContiguousSerializationData(data));
            serializer.DeserializeState(reader);
        }

        int FindSystemIndex(World world, Type systemType)
        {
            var systems = world.GetSystems();
            for (int i = 0; i < systems.Count; i++)
            {
                if (systems[i].System.GetType() == systemType)
                {
                    return i;
                }
            }
            throw new InvalidOperationException($"System {systemType} not found");
        }

        [Test]
        public void Snapshot_RoundTrips_PausedState()
        {
            using var world = CreateWorld();
            var a = world.CreateAccessor(AccessorRole.Unrestricted);

            int idxA = FindSystemIndex(world, typeof(SerEnableSystemA));
            int idxB = FindSystemIndex(world, typeof(SerEnableSystemB));

            // Pause A only.
            a.SetSystemPaused(idxA, true);
            NAssert.IsTrue(a.IsSystemPaused(idxA));
            NAssert.IsFalse(a.IsSystemPaused(idxB));

            var data = SerializeWorld(world);

            // Mutate post-snapshot so we can detect that restore actually overwrote.
            a.SetSystemPaused(idxA, false);
            a.SetSystemPaused(idxB, true);

            DeserializeWorld(world, data);

            NAssert.IsTrue(a.IsSystemPaused(idxA), "Snapshotted paused state restored on A");
            NAssert.IsFalse(a.IsSystemPaused(idxB), "Snapshotted unpaused state restored on B");
        }

        [Test]
        public void Snapshot_DoesNotRoundTrip_ChannelState()
        {
            using var world = CreateWorld();
            var a = world.CreateAccessor(AccessorRole.Unrestricted);
            int idxA = FindSystemIndex(world, typeof(SerEnableSystemA));

            // Channel state at snapshot time: User disabled.
            a.SetSystemEnabled(idxA, EnableChannel.User, false);

            var data = SerializeWorld(world);

            // Flip the channel post-snapshot — restore should NOT touch this.
            a.SetSystemEnabled(idxA, EnableChannel.User, true);
            NAssert.IsTrue(a.IsSystemEnabled(idxA, EnableChannel.User));

            DeserializeWorld(world, data);

            // Channel state is still whatever it was *before* the restore;
            // the snapshot did not carry it.
            NAssert.IsTrue(
                a.IsSystemEnabled(idxA, EnableChannel.User),
                "Channel state is ephemeral and is not overwritten by deserialize"
            );
        }

        [Test]
        public void Snapshot_PausedState_FollowsSystemIdentity_AcrossRegistrationReorder()
        {
            // Paused state serializes by system identity (name + ordinal),
            // not by index — a pause must land on the same system even when
            // the live world registered its systems in a different order
            // (e.g. a patch reordered registration code).
            var globals = TestTemplate
                .Named("TestGlobals")
                .Extending(TrecsTemplates.Globals.Template)
                .Build();

            using var worldAb = new WorldBuilder()
                .SetSettings(new WorldSettings())
                .AddTemplate(globals)
                .AddSystem(new SerEnableSystemA())
                .AddSystem(new SerEnableSystemB())
                .BuildAndInitialize();
            using var worldBa = new WorldBuilder()
                .SetSettings(new WorldSettings())
                .AddTemplate(globals)
                .AddSystem(new SerEnableSystemB())
                .AddSystem(new SerEnableSystemA())
                .BuildAndInitialize();

            var accessorAb = worldAb.CreateAccessor(AccessorRole.Unrestricted);
            accessorAb.SetSystemPaused(FindSystemIndex(worldAb, typeof(SerEnableSystemB)), true);

            var data = SerializeWorld(worldAb);
            DeserializeWorld(worldBa, data);

            var accessorBa = worldBa.CreateAccessor(AccessorRole.Unrestricted);
            NAssert.IsTrue(
                accessorBa.IsSystemPaused(FindSystemIndex(worldBa, typeof(SerEnableSystemB))),
                "Pause should follow system B's identity, not its saved index"
            );
            NAssert.IsFalse(
                accessorBa.IsSystemPaused(FindSystemIndex(worldBa, typeof(SerEnableSystemA))),
                "System A occupies B's old index but must not inherit B's pause"
            );
        }

        [Test]
        public void Snapshot_PausedStateForRemovedSystem_IsDroppedWithoutFailing()
        {
            // A snapshot pausing a system the live world no longer has must
            // still load (the pause is dropped with a warning) — system
            // removal should not invalidate saved state.
            var globals = TestTemplate
                .Named("TestGlobals")
                .Extending(TrecsTemplates.Globals.Template)
                .Build();

            using var worldAb = new WorldBuilder()
                .SetSettings(new WorldSettings())
                .AddTemplate(globals)
                .AddSystem(new SerEnableSystemA())
                .AddSystem(new SerEnableSystemB())
                .BuildAndInitialize();
            using var worldA = new WorldBuilder()
                .SetSettings(new WorldSettings())
                .AddTemplate(globals)
                .AddSystem(new SerEnableSystemA())
                .BuildAndInitialize();

            var accessorAb = worldAb.CreateAccessor(AccessorRole.Unrestricted);
            accessorAb.SetSystemPaused(FindSystemIndex(worldAb, typeof(SerEnableSystemB)), true);

            var data = SerializeWorld(worldAb);
            DeserializeWorld(worldA, data);

            var accessorA = worldA.CreateAccessor(AccessorRole.Unrestricted);
            NAssert.IsFalse(
                accessorA.IsSystemPaused(FindSystemIndex(worldA, typeof(SerEnableSystemA))),
                "A inherits nothing from the dropped pause of the removed system B"
            );
        }

        [Test]
        public void Snapshot_RoundTrips_PausedState_AcrossManySystems()
        {
            using var world = CreateWorld();
            var a = world.CreateAccessor(AccessorRole.Unrestricted);

            // Pause every system, snapshot, then mutate to unpaused, then restore.
            var systemCount = world.GetSystems().Count;
            for (int i = 0; i < systemCount; i++)
            {
                a.SetSystemPaused(i, true);
            }

            var data = SerializeWorld(world);

            for (int i = 0; i < systemCount; i++)
            {
                a.SetSystemPaused(i, false);
            }

            DeserializeWorld(world, data);

            for (int i = 0; i < systemCount; i++)
            {
                NAssert.IsTrue(a.IsSystemPaused(i), $"System {i} stayed paused after restore");
            }
        }
    }
}
