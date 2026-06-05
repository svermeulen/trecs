using NUnit.Framework;
using Trecs.Serialization;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Unit tests for <see cref="BlobFactory"/>: content-addressed dedup (equal descriptors map to
    /// one id and build once), eviction → re-derive (an evicted interned blob is rebuilt from its
    /// descriptor on re-acquire), distinct descriptors map to distinct ids, and both the native and
    /// managed factory shapes.
    /// </summary>
    [TestFixture]
    public class BlobInternerTests
    {
        // Small blittable descriptor; registered for serialization so it can be hashed.
        readonly struct SphereDescriptor
        {
            public readonly float Radius;

            public SphereDescriptor(float radius)
            {
                Radius = radius;
            }
        }

        // Native blob derived from the descriptor.
        readonly struct SphereBlob
        {
            public readonly float RadiusSquared;

            public SphereBlob(float radiusSquared)
            {
                RadiusSquared = radiusSquared;
            }
        }

        static TestEnvironment CreateEnv(BlobCacheSettings blobCacheSettings = null)
        {
            return EcsTestHelper.CreateEnvironment(
                builder =>
                {
                    builder.RegisterSerializer(new BlitSerializer<SphereDescriptor>());
                    if (blobCacheSettings != null)
                    {
                        builder.SetBlobCacheSettings(blobCacheSettings);
                    }
                },
                TestTemplates.SimpleAlpha
            );
        }

        [Test]
        public void Intern_EqualDescriptors_DedupToSameIdAndBuildOnce()
        {
            int builds = 0;
            using var env = CreateEnv();
            var world = env.Accessor;

            NativeSharedAnchor.Register<SphereDescriptor, SphereBlob>(
                world,
                d =>
                {
                    builds++;
                    return new SphereBlob(d.Radius * d.Radius);
                }
            );

            var id1 = world.BlobFactory.Intern(new SphereDescriptor(2f));
            var id2 = world.BlobFactory.Intern(new SphereDescriptor(2f));

            NAssert.AreEqual(id1, id2, "Equal descriptors must intern to the same id");

            var ptr = NativeSharedPtr.Acquire<SphereBlob>(world, id1);
            NAssert.AreEqual(4f, ptr.Read(world).Value.RadiusSquared);
            NAssert.AreEqual(1, builds, "Blob should build exactly once despite two interns");

            ptr.Dispose(world);
        }

        [Test]
        public void Intern_DistinctDescriptors_ProduceDistinctIds()
        {
            using var env = CreateEnv();
            var world = env.Accessor;

            NativeSharedAnchor.Register<SphereDescriptor, SphereBlob>(
                world,
                d => new SphereBlob(d.Radius * d.Radius)
            );

            var id1 = world.BlobFactory.Intern(new SphereDescriptor(2f));
            var id2 = world.BlobFactory.Intern(new SphereDescriptor(3f));

            NAssert.AreNotEqual(id1, id2, "Distinct descriptors must intern to distinct ids");
        }

        [Test]
        public void Intern_AfterEviction_ReDerivesFromDescriptor()
        {
            int builds = 0;
            // Cap inactive native blobs at zero with a 1.0 high-water mark so dropping the only
            // handle immediately evicts the bytes inline.
            using var env = CreateEnv(
                new BlobCacheSettings
                {
                    MaxInactiveNativeBlobsMb = 0f,
                    MaxInactiveManagedBlobsCount = 0,
                    HighWaterMarkMultiplier = 1f,
                }
            );
            var world = env.Accessor;

            NativeSharedAnchor.Register<SphereDescriptor, SphereBlob>(
                world,
                d =>
                {
                    builds++;
                    return new SphereBlob(d.Radius * d.Radius);
                }
            );

            var id = world.BlobFactory.Intern(new SphereDescriptor(5f));

            var ptr1 = NativeSharedPtr.Acquire<SphereBlob>(world, id);
            NAssert.AreEqual(25f, ptr1.Read(world).Value.RadiusSquared);
            NAssert.AreEqual(1, builds);
            // Dropping the last handle makes the blob inactive; the zero cap evicts its bytes.
            ptr1.Dispose(world);

            // The descriptor source is retained across eviction, so the id is still registered and
            // re-acquiring re-runs the builder to re-materialize the bytes.
            NAssert.IsTrue(world.BlobFactory.IsRegistered(id));

            var ptr2 = NativeSharedPtr.Acquire<SphereBlob>(world, id);
            NAssert.AreEqual(25f, ptr2.Read(world).Value.RadiusSquared);
            NAssert.AreEqual(
                2,
                builds,
                "Evicted interned blob should re-derive from its descriptor"
            );

            ptr2.Dispose(world);
        }

        [Test]
        public void Acquire_FromDescriptor_BuildsAndReturnsHandle()
        {
            using var env = CreateEnv();
            var world = env.Accessor;

            SharedAnchor.Register<SphereDescriptor, string>(world, d => $"sphere-{d.Radius}");

            var ptr = SharedPtr.Acquire<SphereDescriptor, string>(world, new SphereDescriptor(7f));

            NAssert.AreEqual("sphere-7", ptr.Get(world));

            ptr.Dispose(world);
        }

        [Test]
        public void SharedAnchor_AcquireFromDescriptor_BuildsOnMissAndReturnsHandle()
        {
            int builds = 0;
            using var env = CreateEnv();
            var world = env.Accessor;

            SharedAnchor.Register<SphereDescriptor, string>(
                world,
                d =>
                {
                    builds++;
                    return $"sphere-{d.Radius}";
                }
            );

            // The anchor counterpart to SharedPtr.Acquire<TDesc,T>: hashes the descriptor, builds on
            // the miss, and returns a pinning anchor.
            var anchor = SharedAnchor.Acquire<SphereDescriptor, string>(
                world,
                new SphereDescriptor(7f)
            );

            NAssert.AreEqual("sphere-7", anchor.Get(world));
            NAssert.AreEqual(1, builds, "Builder should run once on the cache miss");

            anchor.Dispose(world);
        }

        [Test]
        public void SharedAnchor_AcquireFromDescriptor_EqualDescriptors_DedupAndBuildOnce()
        {
            int builds = 0;
            using var env = CreateEnv();
            var world = env.Accessor;

            SharedAnchor.Register<SphereDescriptor, string>(
                world,
                d =>
                {
                    builds++;
                    return $"sphere-{d.Radius}";
                }
            );

            var a = SharedAnchor.Acquire<SphereDescriptor, string>(world, new SphereDescriptor(2f));
            var b = SharedAnchor.Acquire<SphereDescriptor, string>(world, new SphereDescriptor(2f));

            NAssert.AreEqual(a.BlobId, b.BlobId, "Equal descriptors must pin the same blob");
            NAssert.AreEqual(1, builds, "Identical descriptors must dedup to a single build");

            a.Dispose(world);
            b.Dispose(world);
        }

        [Test]
        public void NativeSharedAnchor_AcquireFromDescriptor_BuildsOnMissAndReturnsHandle()
        {
            int builds = 0;
            using var env = CreateEnv();
            var world = env.Accessor;

            NativeSharedAnchor.Register<SphereDescriptor, SphereBlob>(
                world,
                d =>
                {
                    builds++;
                    return new SphereBlob(d.Radius * d.Radius);
                }
            );

            var anchor = NativeSharedAnchor.Acquire<SphereDescriptor, SphereBlob>(
                world,
                new SphereDescriptor(7f)
            );

            NAssert.AreEqual(49f, anchor.Get(world).RadiusSquared);
            NAssert.AreEqual(1, builds, "Builder should run once on the cache miss");

            anchor.Dispose(world);
        }

        [Test]
        public void Intern_UnregisteredDescriptorType_Throws()
        {
            using var env = CreateEnv();
            var world = env.Accessor;

            NAssert.Throws<TrecsException>(() =>
                world.BlobFactory.Intern(new SphereDescriptor(1f))
            );
        }
    }
}
