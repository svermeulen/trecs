using System.Collections.Generic;
using NUnit.Framework;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class AccessorAllocTests
    {
        #region UniquePtr via WorldAccessor

        [Test]
        public void Accessor_AllocUnique_ReturnsValidPtr()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var ptr = UniquePtr.Alloc<List<string>>(a.Heap);

            NAssert.IsFalse(ptr.IsNull);

            ptr.Dispose(a);
        }

        [Test]
        public void Accessor_AllocUnique_WithValue_GetReturnsValue()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var original = new List<string> { "hello", "world" };
            var ptr = UniquePtr.Alloc(a.Heap, original);

            var retrieved = ptr.Get(a);
            NAssert.AreSame(original, retrieved);
            NAssert.AreEqual(2, retrieved.Count);

            ptr.Dispose(a);
        }

        [Test]
        public void Accessor_AllocUnique_SetUpdatesValue()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var ptr = UniquePtr.Alloc(a.Heap, new List<string> { "first" });
            var updated = new List<string> { "second" };
            ptr.Set(a, updated);

            NAssert.AreSame(updated, ptr.Get(a));

            ptr.Dispose(a);
        }

        [Test]
        public void Accessor_AllocUnique_DisposeInvalidates()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var ptr = UniquePtr.Alloc(a.Heap, new List<string> { "test" });
            NAssert.IsFalse(ptr.IsNull);

            ptr.Dispose(a);

            // TryGet should fail after dispose
            NAssert.IsFalse(ptr.TryGet(a, out _));
        }

        #endregion

        #region SharedPtr via WorldAccessor

        [Test]
        public void Accessor_AllocShared_ReturnsValidPtr()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var blob = new List<string> { "shared" };
            var ptr = SharedPtr.Alloc(a.Heap, BlobIdGenerator.FromKey(1), blob);

            NAssert.IsFalse(ptr.IsNull);

            var retrieved = ptr.Get(a);
            NAssert.AreSame(blob, retrieved);

            ptr.Dispose(a);
        }

        [Test]
        public void Accessor_AllocShared_Clone_IndependentLifetime()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            var blob = new List<string> { "cloneable" };
            var ptr = SharedPtr.Alloc(a.Heap, BlobIdGenerator.FromKey(1), blob);
            var clone = ptr.Clone(a);

            NAssert.IsFalse(clone.IsNull);

            // Dispose original
            ptr.Dispose(a);

            // Clone should still be accessible
            NAssert.IsTrue(clone.CanGet(a));
            var retrieved = clone.Get(a);
            NAssert.AreSame(blob, retrieved);

            clone.Dispose(a);
        }

        #endregion
    }
}
