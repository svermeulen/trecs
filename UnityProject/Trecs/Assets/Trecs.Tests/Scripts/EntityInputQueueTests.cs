using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class EntityInputQueueTests
    {
        #region AddInput / TryGetInput

        [Test]
        public void InputQueue_AddInput_TryGetReturnsValue()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var inputQueue = env.World.GetEntityInputQueue();

            var handle = a.AddEntity(TestTags.Alpha)
                .Set(new TestInt { Value = 0 })
                .AssertComplete()
                .Handle;
            a.World.Submit();

            inputQueue.AddInput(frame: 0, handle, new TestInt { Value = 42 });

            bool found = inputQueue.TryGetInput<TestInt>(0, handle, out var result);
            NAssert.IsTrue(found);
            NAssert.AreEqual(42, result.Value);
        }

        [Test]
        public void InputQueue_AddInput_HasInputFrameReturnsTrue()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var inputQueue = env.World.GetEntityInputQueue();

            var handle = a.AddEntity(TestTags.Alpha)
                .Set(new TestInt { Value = 0 })
                .AssertComplete()
                .Handle;
            a.World.Submit();

            inputQueue.AddInput(frame: 5, handle, new TestInt { Value = 99 });

            NAssert.IsTrue(inputQueue.HasInputFrame<TestInt>(5, handle));
            NAssert.IsFalse(inputQueue.HasInputFrame<TestInt>(4, handle));
            NAssert.IsFalse(inputQueue.HasInputFrame<TestInt>(6, handle));
        }

        [Test]
        public void InputQueue_NoInput_TryGetReturnsFalse()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var inputQueue = env.World.GetEntityInputQueue();

            var handle = a.AddEntity(TestTags.Alpha)
                .Set(new TestInt { Value = 0 })
                .AssertComplete()
                .Handle;
            a.World.Submit();

            bool found = inputQueue.TryGetInput<TestInt>(0, handle, out _);
            NAssert.IsFalse(found);
        }

        #endregion

        #region SetInput (Overwrite)

        [Test]
        public void InputQueue_SetInput_OverwritesExisting()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var inputQueue = env.World.GetEntityInputQueue();

            var handle = a.AddEntity(TestTags.Alpha)
                .Set(new TestInt { Value = 0 })
                .AssertComplete()
                .Handle;
            a.World.Submit();

            inputQueue.AddInput(frame: 0, handle, new TestInt { Value = 10 });
            inputQueue.SetInput(frame: 0, handle, new TestInt { Value = 20 });

            inputQueue.TryGetInput<TestInt>(0, handle, out var result);
            NAssert.AreEqual(20, result.Value);
        }

        [Test]
        public void InputQueue_SetInput_CreatesIfMissing()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var inputQueue = env.World.GetEntityInputQueue();

            var handle = a.AddEntity(TestTags.Alpha)
                .Set(new TestInt { Value = 0 })
                .AssertComplete()
                .Handle;
            a.World.Submit();

            inputQueue.SetInput(frame: 0, handle, new TestInt { Value = 55 });

            inputQueue.TryGetInput<TestInt>(0, handle, out var result);
            NAssert.AreEqual(55, result.Value);
        }

        #endregion

        #region Multiple Frames

        [Test]
        public void InputQueue_MultipleFrames_IndependentValues()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var inputQueue = env.World.GetEntityInputQueue();

            var handle = a.AddEntity(TestTags.Alpha)
                .Set(new TestInt { Value = 0 })
                .AssertComplete()
                .Handle;
            a.World.Submit();

            inputQueue.AddInput(frame: 0, handle, new TestInt { Value = 100 });
            inputQueue.AddInput(frame: 1, handle, new TestInt { Value = 200 });
            inputQueue.AddInput(frame: 2, handle, new TestInt { Value = 300 });

            inputQueue.TryGetInput<TestInt>(0, handle, out var r0);
            inputQueue.TryGetInput<TestInt>(1, handle, out var r1);
            inputQueue.TryGetInput<TestInt>(2, handle, out var r2);

            NAssert.AreEqual(100, r0.Value);
            NAssert.AreEqual(200, r1.Value);
            NAssert.AreEqual(300, r2.Value);
        }

        #endregion

        #region Multiple Entities

        [Test]
        public void InputQueue_MultipleEntities_IndependentValues()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var inputQueue = env.World.GetEntityInputQueue();

            var h1 = a.AddEntity(TestTags.Alpha)
                .Set(new TestInt { Value = 0 })
                .AssertComplete()
                .Handle;
            var h2 = a.AddEntity(TestTags.Alpha)
                .Set(new TestInt { Value = 0 })
                .AssertComplete()
                .Handle;
            a.World.Submit();

            inputQueue.AddInput(frame: 0, h1, new TestInt { Value = 10 });
            inputQueue.AddInput(frame: 0, h2, new TestInt { Value = 20 });

            inputQueue.TryGetInput<TestInt>(0, h1, out var r1);
            inputQueue.TryGetInput<TestInt>(0, h2, out var r2);

            NAssert.AreEqual(10, r1.Value);
            NAssert.AreEqual(20, r2.Value);
        }

        #endregion

        #region ClearFutureInputsAfterOrAt

        [Test]
        public void InputQueue_ClearFuture_RemovesFromFrameOnward()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var inputQueue = env.World.GetEntityInputQueue();

            var handle = a.AddEntity(TestTags.Alpha)
                .Set(new TestInt { Value = 0 })
                .AssertComplete()
                .Handle;
            a.World.Submit();

            inputQueue.AddInput(frame: 5, handle, new TestInt { Value = 50 });
            inputQueue.AddInput(frame: 10, handle, new TestInt { Value = 100 });
            inputQueue.AddInput(frame: 15, handle, new TestInt { Value = 150 });

            inputQueue.ClearFutureInputsAfterOrAt(10);

            NAssert.IsTrue(inputQueue.HasInputFrame<TestInt>(5, handle), "Frame 5 should remain");
            NAssert.IsFalse(
                inputQueue.HasInputFrame<TestInt>(10, handle),
                "Frame 10 should be cleared"
            );
            NAssert.IsFalse(
                inputQueue.HasInputFrame<TestInt>(15, handle),
                "Frame 15 should be cleared"
            );
        }

        #endregion

        #region ClearInputsBeforeOrAt

        [Test]
        public void InputQueue_ClearPast_RemovesUpToFrame()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var inputQueue = env.World.GetEntityInputQueue();

            var handle = a.AddEntity(TestTags.Alpha)
                .Set(new TestInt { Value = 0 })
                .AssertComplete()
                .Handle;
            a.World.Submit();

            inputQueue.AddInput(frame: 5, handle, new TestInt { Value = 50 });
            inputQueue.AddInput(frame: 10, handle, new TestInt { Value = 100 });
            inputQueue.AddInput(frame: 15, handle, new TestInt { Value = 150 });

            inputQueue.ClearInputsBeforeOrAt(10);

            NAssert.IsFalse(
                inputQueue.HasInputFrame<TestInt>(5, handle),
                "Frame 5 should be cleared"
            );
            NAssert.IsFalse(
                inputQueue.HasInputFrame<TestInt>(10, handle),
                "Frame 10 should be cleared"
            );
            NAssert.IsTrue(inputQueue.HasInputFrame<TestInt>(15, handle), "Frame 15 should remain");
        }

        #endregion

        #region ClearAllInputs

        [Test]
        public void InputQueue_ClearAll_RemovesEverything()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var inputQueue = env.World.GetEntityInputQueue();

            var handle = a.AddEntity(TestTags.Alpha)
                .Set(new TestInt { Value = 0 })
                .AssertComplete()
                .Handle;
            a.World.Submit();

            inputQueue.AddInput(frame: 0, handle, new TestInt { Value = 10 });
            inputQueue.AddInput(frame: 1, handle, new TestInt { Value = 20 });

            inputQueue.ClearAllInputs();

            NAssert.IsFalse(inputQueue.HasInputFrame<TestInt>(0, handle));
            NAssert.IsFalse(inputQueue.HasInputFrame<TestInt>(1, handle));
        }

        #endregion

        #region AddInput Duplicate Throws

        [Test]
        public void InputQueue_AddInput_DuplicateThrows()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var inputQueue = env.World.GetEntityInputQueue();

            var handle = a.AddEntity(TestTags.Alpha)
                .Set(new TestInt { Value = 0 })
                .AssertComplete()
                .Handle;
            a.World.Submit();

            inputQueue.AddInput(frame: 0, handle, new TestInt { Value = 10 });

            NAssert.Catch(() =>
            {
                inputQueue.AddInput(frame: 0, handle, new TestInt { Value = 20 });
            });
        }

        #endregion

        #region ApplyInputs — Removed Entity

        // Applying an input whose target entity no longer exists should be
        // impossible under normal operation (inputs are applied just-in-time
        // before the fixed tick, with no Submit() between the input systems and
        // ApplyInputs). It therefore trips a debug assert. In a release build the
        // assert is stripped and the input is silently dropped to avoid crashing.
        [Test]
        public void InputQueue_ApplyInputs_RemovedEntity_AssertsInDebug()
        {
            if (!TrecsDebugAssert.IsEnabled)
            {
                // Release-style build: assert is stripped, input is dropped
                // silently. Nothing observable to assert here.
                NAssert.Pass("Debug asserts disabled — release behavior is silent drop.");
            }

            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;
            var inputQueue = env.World.GetEntityInputQueue();

            var handle = a.AddEntity(TestTags.Alpha)
                .Set(new TestInt { Value = 0 })
                .AssertComplete()
                .Handle;
            a.World.Submit();

            inputQueue.AddInput(frame: 0, handle, new TestInt { Value = 42 });

            // Remove the entity and flush the removal so the handle no longer
            // resolves by the time inputs are applied.
            a.RemoveEntity(handle);
            a.World.Submit();
            NAssert.IsFalse(handle.Exists(a));

            NAssert.Throws<TrecsException>(() => inputQueue.ApplyInputs(currentFixedFrame: 0));
        }

        #endregion
    }
}
