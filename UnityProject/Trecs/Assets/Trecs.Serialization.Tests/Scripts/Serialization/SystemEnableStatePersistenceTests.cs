using System;
using System.IO;
using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Serialization.Tests
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
            _serializerRegistry = SerializationFactory.CreateRegistry();
        }

        World CreateWorld()
        {
            var globals = new Template(
                debugName: "TestGlobals",
                localBaseTemplates: new Template[] { TrecsTemplates.Globals.Template },
                partitions: Array.Empty<TagSet>(),
                localComponentDeclarations: Array.Empty<IComponentDeclaration>(),
                localTags: Array.Empty<Tag>()
            );

            return new WorldBuilder()
                .SetSettings(new WorldSettings())
                .AddTemplate(globals)
                .AddSystem(new SerEnableSystemA())
                .AddSystem(new SerEnableSystemB())
                .AddBlobStore(
                    new BlobStoreInMemory(
                        new BlobStoreInMemorySettings { MaxMemoryCacheMb = 100 },
                        null
                    )
                )
                .BuildAndInitialize();
        }

        byte[] SerializeWorld(World world)
        {
            var serializer = new WorldStateSerializer(world);
            using var writer = new BinarySerializationWriter(_serializerRegistry);
            writer.Start(version: 1, includeTypeChecks: false);
            serializer.SerializeState(writer);

            using var outputStream = new MemoryStream();
            using var outputWriter = new BinaryWriter(outputStream);
            writer.Complete(outputWriter);
            return outputStream.ToArray();
        }

        void DeserializeWorld(World world, byte[] data)
        {
            var serializer = new WorldStateSerializer(world);
            using var inputStream = new MemoryStream(data);
            using var inputReader = new BinaryReader(inputStream);
            var reader = new BinarySerializationReader(_serializerRegistry);
            reader.Start(inputReader);
            serializer.DeserializeState(reader);
        }

        int FindSystemIndex(World world, Type systemType)
        {
            for (int i = 0; i < world.SystemCount; i++)
            {
                if (world.GetSystemMetadata(i).System.GetType() == systemType)
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
        public void Snapshot_RoundTrips_PausedState_AcrossManySystems()
        {
            using var world = CreateWorld();
            var a = world.CreateAccessor(AccessorRole.Unrestricted);

            // Pause every system, snapshot, then mutate to unpaused, then restore.
            for (int i = 0; i < world.SystemCount; i++)
            {
                a.SetSystemPaused(i, true);
            }

            var data = SerializeWorld(world);

            for (int i = 0; i < world.SystemCount; i++)
            {
                a.SetSystemPaused(i, false);
            }

            DeserializeWorld(world, data);

            for (int i = 0; i < world.SystemCount; i++)
            {
                NAssert.IsTrue(a.IsSystemPaused(i), $"System {i} stayed paused after restore");
            }
        }
    }
}
