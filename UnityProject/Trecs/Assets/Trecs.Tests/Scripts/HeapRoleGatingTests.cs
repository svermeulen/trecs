using System.Collections.Generic;
using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Negative coverage for <see cref="WorldAccessor.AssertCanMutateHeap"/>:
    /// only Fixed-role and Unrestricted-role accessors are allowed to mutate
    /// the heap (Alloc, Write, Set, Clone, Acquire, Dispose, EnsureCapacity).
    /// Variable and Input-system accessors must be rejected — Variable because
    /// the heap is simulation state and would desync on every variable tick;
    /// Input because all input-side allocations must be frame-scoped to match
    /// the input replay lifetime.
    ///
    /// The assertion is <c>[Conditional("DEBUG")]</c>; Unity EditMode tests run
    /// in DEBUG so the gate fires here.
    /// </summary>
    [TestFixture]
    public class HeapRoleGatingTests
    {
        TestEnvironment CreateEnv() => EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);

        [Test]
        public void SharedPtrAlloc_FromVariableRoleAccessor_Throws()
        {
            using var env = CreateEnv();
            var variableAccessor = env.World.CreateAccessor(
                AccessorRole.Variable,
                "VariableRoleTest"
            );

            NAssert.Throws<TrecsException>(() =>
                SharedPtr.Alloc(
                    variableAccessor,
                    BlobIdGenerator.FromKey(1),
                    new List<string> { "should-fail" }
                )
            );
        }

        [Test]
        public void UniquePtrAlloc_FromVariableRoleAccessor_Throws()
        {
            using var env = CreateEnv();
            var variableAccessor = env.World.CreateAccessor(
                AccessorRole.Variable,
                "VariableRoleTest"
            );

            // UniquePtr.Alloc routes through the same AssertCanMutateHeap
            // gate as SharedPtr.Alloc, so it must reject the Variable role too.
            NAssert.Throws<TrecsException>(() =>
                UniquePtr.Alloc(variableAccessor, new List<string> { "should-fail" })
            );
        }

        [Test]
        public void SharedPtrAlloc_FromInputAccessor_Throws()
        {
            // Input-system accessors live at role=Variable with isInput=true (see
            // SystemPhaseExtensions.ToAccessorRole + CreateAccessorForSystem). Only
            // the framework-internal CreateAccessorExplicit lets us construct one
            // directly — the public CreateAccessor never sets isInput.
            using var env = CreateEnv();
            var inputAccessor = env.World.CreateAccessorExplicit(
                role: AccessorRole.Variable,
                isInput: true,
                debugName: "InputRoleTest"
            );

            NAssert.Throws<TrecsException>(() =>
                SharedPtr.Alloc(
                    inputAccessor,
                    BlobIdGenerator.FromKey(2),
                    new List<string> { "input-must-frame-scope" }
                )
            );
        }

        [Test]
        public void SharedPtrAllocFrameScoped_FromInputAccessor_Succeeds()
        {
            // Companion positive case: input-system accessors *are* allowed to
            // allocate frame-scoped pointers — that's the prescribed escape hatch
            // baked into the AssertCanMutateHeap error message.
            using var env = CreateEnv();
            var inputAccessor = env.World.CreateAccessorExplicit(
                role: AccessorRole.Variable,
                isInput: true,
                debugName: "InputRoleFrameScopedTest"
            );

            NAssert.DoesNotThrow(() =>
            {
                var ptr = InputSharedPtr.Alloc(
                    inputAccessor,
                    BlobIdGenerator.FromKey(3),
                    new List<string> { "input-frame-scoped" }
                );
                NAssert.IsFalse(ptr.IsNull);
            });
        }

        [Test]
        public void SharedPtrAlloc_FromUnrestrictedAccessor_Succeeds()
        {
            using var env = CreateEnv();
            var unrestrictedAccessor = env.World.CreateAccessor(
                AccessorRole.Unrestricted,
                "UnrestrictedRoleTest"
            );

            NAssert.DoesNotThrow(() =>
            {
                var ptr = SharedPtr.Alloc(
                    unrestrictedAccessor,
                    BlobIdGenerator.FromKey(4),
                    new List<string> { "unrestricted-ok" }
                );
                NAssert.IsFalse(ptr.IsNull);
                ptr.Dispose(unrestrictedAccessor);
            });
        }

        [Test]
        public void SharedPtrAlloc_FromFixedRoleAccessor_Succeeds()
        {
            // Fixed-role is the canonical persistent-heap allocation site (sim
            // owns deterministic state). Asserts the role gate lets it through —
            // the inverse of the Variable/Input rejections above.
            using var env = CreateEnv();
            var fixedAccessor = env.World.CreateAccessor(AccessorRole.Fixed, "FixedRoleTest");

            NAssert.DoesNotThrow(() =>
            {
                var ptr = SharedPtr.Alloc(
                    fixedAccessor,
                    BlobIdGenerator.FromKey(5),
                    new List<string> { "fixed-ok" }
                );
                NAssert.IsFalse(ptr.IsNull);
                ptr.Dispose(fixedAccessor);
            });
        }

        // ---------------------------------------------------------------------
        // Per-verb negative coverage. Alloc is covered by the tests above; these
        // lock in the *other* mutation verbs (Clone, Set, Write, Dispose) across
        // every pointer type. Each test allocs through an Unrestricted-role
        // accessor (state setup), then drives the verb through a Variable-role
        // accessor and asserts it throws. If a refactor drops an
        // `AssertCanMutateHeap()` line from any individual call site, the
        // matching test here flips green→red even though Alloc still works.
        // ---------------------------------------------------------------------

        [Test]
        public void SharedPtrClone_FromVariableRoleAccessor_Throws()
        {
            using var env = CreateEnv();
            var unrestricted = env.Accessor;
            var variableAccessor = env.World.CreateAccessor(
                AccessorRole.Variable,
                "VariableRoleTest"
            );

            var ptr = SharedPtr.Alloc(
                unrestricted,
                BlobIdGenerator.FromKey(10),
                new List<string> { "src" }
            );

            NAssert.Throws<TrecsException>(() => ptr.Clone(variableAccessor));

            ptr.Dispose(unrestricted);
        }

        [Test]
        public void SharedPtrDispose_FromVariableRoleAccessor_Throws()
        {
            using var env = CreateEnv();
            var unrestricted = env.Accessor;
            var variableAccessor = env.World.CreateAccessor(
                AccessorRole.Variable,
                "VariableRoleTest"
            );

            var ptr = SharedPtr.Alloc(
                unrestricted,
                BlobIdGenerator.FromKey(11),
                new List<string> { "src" }
            );

            NAssert.Throws<TrecsException>(() => ptr.Dispose(variableAccessor));

            // Pointer is still live (Dispose was blocked); clean up via the
            // permissive accessor so the env tears down cleanly.
            ptr.Dispose(unrestricted);
        }

        [Test]
        public void NativeSharedPtrClone_FromVariableRoleAccessor_Throws()
        {
            using var env = CreateEnv();
            var unrestricted = env.Accessor;
            var variableAccessor = env.World.CreateAccessor(
                AccessorRole.Variable,
                "VariableRoleTest"
            );

            var ptr = NativeSharedPtr.Alloc<int>(unrestricted, BlobIdGenerator.FromKey(12), 42);

            NAssert.Throws<TrecsException>(() => ptr.Clone(variableAccessor));

            ptr.Dispose(unrestricted);
        }

        [Test]
        public void NativeSharedPtrDispose_FromVariableRoleAccessor_Throws()
        {
            using var env = CreateEnv();
            var unrestricted = env.Accessor;
            var variableAccessor = env.World.CreateAccessor(
                AccessorRole.Variable,
                "VariableRoleTest"
            );

            var ptr = NativeSharedPtr.Alloc<int>(unrestricted, BlobIdGenerator.FromKey(13), 42);

            NAssert.Throws<TrecsException>(() => ptr.Dispose(variableAccessor));

            ptr.Dispose(unrestricted);
        }

        [Test]
        public void UniquePtrSet_FromVariableRoleAccessor_Throws()
        {
            using var env = CreateEnv();
            var unrestricted = env.Accessor;
            var variableAccessor = env.World.CreateAccessor(
                AccessorRole.Variable,
                "VariableRoleTest"
            );

            var ptr = UniquePtr.Alloc(unrestricted, new List<string> { "initial" });

            NAssert.Throws<TrecsException>(() =>
                ptr.Set(variableAccessor, new List<string> { "replacement" })
            );

            ptr.Dispose(unrestricted);
        }

        [Test]
        public void UniquePtrDispose_FromVariableRoleAccessor_Throws()
        {
            using var env = CreateEnv();
            var unrestricted = env.Accessor;
            var variableAccessor = env.World.CreateAccessor(
                AccessorRole.Variable,
                "VariableRoleTest"
            );

            var ptr = UniquePtr.Alloc(unrestricted, new List<string> { "val" });

            NAssert.Throws<TrecsException>(() => ptr.Dispose(variableAccessor));

            ptr.Dispose(unrestricted);
        }

        [Test]
        public void NativeUniquePtrWrite_FromVariableRoleAccessor_Throws()
        {
            using var env = CreateEnv();
            var unrestricted = env.Accessor;
            var variableAccessor = env.World.CreateAccessor(
                AccessorRole.Variable,
                "VariableRoleTest"
            );

            var ptr = NativeUniquePtr.Alloc<int>(unrestricted, 7);

            NAssert.Throws<TrecsException>(() => ptr.Write(variableAccessor));

            ptr.Dispose(unrestricted);
        }

        [Test]
        public void NativeUniquePtrWrite_FromVariableRoleNativeResolver_Throws()
        {
            // Burst-job-side gate, mirroring TrecsList's
            // NativeWrite_FromVariableRoleNativeWorldAccessor_Throws. The
            // NativeHeapResolver pulled off a Variable-role accessor's
            // ToNative() carries _canMutateHeap = 0, so a job that holds only
            // the resolver can't bypass the role check either.
            using var env = CreateEnv();
            var unrestricted = env.Accessor;
            var variableAccessor = env.World.CreateAccessor(
                AccessorRole.Variable,
                "VariableRoleTest"
            );

            var ptr = NativeUniquePtr.Alloc<int>(unrestricted, 9);
            var nativeVariable = variableAccessor.ToNative();
            var resolver = nativeVariable.ChunkStoreResolver;

            NAssert.Throws<TrecsException>(() => ptr.Write(in resolver));

            ptr.Dispose(unrestricted);
        }

        [Test]
        public void NativeUniquePtrDispose_FromVariableRoleAccessor_Throws()
        {
            using var env = CreateEnv();
            var unrestricted = env.Accessor;
            var variableAccessor = env.World.CreateAccessor(
                AccessorRole.Variable,
                "VariableRoleTest"
            );

            var ptr = NativeUniquePtr.Alloc<int>(unrestricted, 11);

            NAssert.Throws<TrecsException>(() => ptr.Dispose(variableAccessor));

            ptr.Dispose(unrestricted);
        }
    }
}
