using System;
using System.Collections.Generic;
using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class SimpleReactiveBufferTests
    {
        [Test]
        public void Flush_RunsBufferedActionsInOrder()
        {
            var buffer = new SimpleReactiveBuffer();
            var order = new List<int>();

            buffer.AddAction(() => order.Add(1));
            buffer.AddAction(() => order.Add(2));

            // Buffered, not yet run.
            NAssert.AreEqual(0, order.Count);

            buffer.Flush();

            CollectionAssert.AreEqual(new[] { 1, 2 }, order);
        }

        [Test]
        public void Flush_WhenActionThrows_StillResetsFlushingState()
        {
            // Regression: Flush lacked a try/finally, so a throwing buffered action left
            // _isFlushing stuck true. After that, AddAction ran every action immediately
            // instead of buffering it — silently defeating the buffer for the object's
            // lifetime. After a throwing flush, buffering must still work normally.
            var buffer = new SimpleReactiveBuffer();

            buffer.AddAction(() => throw new InvalidOperationException("boom"));

            NAssert.Throws<InvalidOperationException>(() => buffer.Flush());

            // A subsequently added action must be BUFFERED (deferred), not run immediately.
            var ran = false;
            buffer.AddAction(() => ran = true);
            NAssert.IsFalse(ran, "Action ran immediately — _isFlushing was left stuck true.");

            // It runs on the next explicit Flush.
            buffer.Flush();
            NAssert.IsTrue(ran);
        }
    }
}
