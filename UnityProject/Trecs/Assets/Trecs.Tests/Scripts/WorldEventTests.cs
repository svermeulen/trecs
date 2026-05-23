using NUnit.Framework;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class WorldEventTests
    {
        #region SubmissionCompleted

        [Test]
        public void Event_SubmissionCompleted_FiresOnSubmit()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            int callCount = 0;
            var sub = a.Events.OnSubmissionCompleted(() => callCount++);

            a.AddEntity(TestTags.Alpha).AssertComplete();
            a.Submit();

            NAssert.Greater(callCount, 0, "SubmissionCompleted should fire on Submit");
            sub.Dispose();
        }

        [Test]
        public void Event_SubmissionCompleted_FiresEachSubmit()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            int callCount = 0;
            var sub = a.Events.OnSubmissionCompleted(() => callCount++);

            a.AddEntity(TestTags.Alpha).AssertComplete();
            a.Submit();
            int afterFirst = callCount;

            a.AddEntity(TestTags.Alpha).AssertComplete();
            a.Submit();

            NAssert.Greater(callCount, afterFirst, "Should fire on each submit");
            sub.Dispose();
        }

        #endregion

        #region Observer Dispose Stops Callbacks

        [Test]
        public void Event_SubscriptionDisposed_NoMoreCallbacks()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            int callCount = 0;
            var sub = a.Events.OnSubmissionCompleted(() => callCount++);

            a.AddEntity(TestTags.Alpha).AssertComplete();
            a.Submit();
            NAssert.AreEqual(1, callCount);

            sub.Dispose();

            a.AddEntity(TestTags.Alpha).AssertComplete();
            a.Submit();
            NAssert.AreEqual(1, callCount, "Should not fire after dispose");
        }

        #endregion

        #region Multiple Observers

        [Test]
        public void Event_MultipleObservers_AllFire()
        {
            using var env = EcsTestHelper.CreateEnvironment(TestTemplates.SimpleAlpha);
            var a = env.Accessor;

            int count1 = 0,
                count2 = 0;
            var sub1 = a.Events.OnSubmissionCompleted(() => count1++);
            var sub2 = a.Events.OnSubmissionCompleted(() => count2++);

            a.AddEntity(TestTags.Alpha).AssertComplete();
            a.Submit();

            NAssert.AreEqual(1, count1);
            NAssert.AreEqual(1, count2);

            sub1.Dispose();
            sub2.Dispose();
        }

        #endregion
    }
}
