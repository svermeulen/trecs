using System.Collections.Generic;
using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Negative coverage for <see cref="HeapAccessor.AssertCanAllocatePersistent"/>:
    /// only Fixed-role and Unrestricted-role accessors are allowed to allocate
    /// persistent heap pointers. Variable and Input-system accessors must be
    /// rejected — Variable because it has no business holding persistent heap
    /// handles at all, Input because all input-side allocations must be
    /// frame-scoped to match the input replay lifetime.
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
                    variableAccessor.Heap,
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

            // UniquePtr.Alloc routes through the same AssertCanAllocatePersistent
            // gate as SharedPtr.Alloc, so it must reject the Variable role too.
            NAssert.Throws<TrecsException>(() =>
                UniquePtr.Alloc(variableAccessor.Heap, new List<string> { "should-fail" })
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
                    inputAccessor.Heap,
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
            // baked into the AssertCanAllocatePersistent error message.
            using var env = CreateEnv();
            var inputAccessor = env.World.CreateAccessorExplicit(
                role: AccessorRole.Variable,
                isInput: true,
                debugName: "InputRoleFrameScopedTest"
            );

            NAssert.DoesNotThrow(() =>
            {
                var ptr = SharedPtr.AllocFrameScoped(
                    inputAccessor.Heap,
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
                    unrestrictedAccessor.Heap,
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
                    fixedAccessor.Heap,
                    BlobIdGenerator.FromKey(5),
                    new List<string> { "fixed-ok" }
                );
                NAssert.IsFalse(ptr.IsNull);
                ptr.Dispose(fixedAccessor);
            });
        }
    }
}
