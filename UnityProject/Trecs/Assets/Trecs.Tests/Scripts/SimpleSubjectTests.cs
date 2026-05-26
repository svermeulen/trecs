using System;
using System.Collections.Generic;
using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class SimpleSubjectTests
    {
        [Test]
        public void Unsubscribe_DuplicateHandler_RemovesCorrectSubscription()
        {
            var subject = new SimpleSubject();
            var calls = new List<string>();

            Action handler = () => calls.Add("called");

            var sub1 = subject.Subscribe(handler, priority: 5);
            var sub2 = subject.Subscribe(handler, priority: 10);

            NAssert.AreEqual(2, subject.NumObservers);

            sub2.Dispose();

            NAssert.AreEqual(
                1,
                subject.NumObservers,
                "Disposing sub2 should remove exactly one subscription"
            );

            subject.Invoke();

            NAssert.AreEqual(
                1,
                calls.Count,
                "Only the remaining subscription (priority 5) should fire"
            );
        }

        [Test]
        public void Unsubscribe_DuplicateHandler_DuringInvoke_RemovesCorrectSubscription()
        {
            var subject = new SimpleSubject();
            var invokeOrder = new List<int>();
            IDisposable sub2 = null;

            Action handler = () =>
            {
                invokeOrder.Add(1);
                sub2?.Dispose();
                sub2 = null;
            };

            subject.Subscribe(handler, priority: 5);
            sub2 = subject.Subscribe(handler, priority: 10);

            subject.Invoke();

            NAssert.AreEqual(
                1,
                subject.NumObservers,
                "After invoke with deferred unsubscribe, one subscription should remain"
            );

            invokeOrder.Clear();
            subject.Invoke();

            NAssert.AreEqual(
                1,
                invokeOrder.Count,
                "Only the remaining subscription should fire on second invoke"
            );
        }

        [Test]
        public void Subscribe_WithPriority_InvokesInPriorityOrder()
        {
            var subject = new SimpleSubject();
            var order = new List<int>();

            subject.Subscribe(() => order.Add(3), priority: 30);
            subject.Subscribe(() => order.Add(1), priority: 10);
            subject.Subscribe(() => order.Add(2), priority: 20);

            subject.Invoke();

            NAssert.AreEqual(
                new List<int> { 1, 2, 3 },
                order,
                "Observers should be invoked in ascending priority order"
            );
        }

        [Test]
        public void Unsubscribe_RemovesObserver()
        {
            var subject = new SimpleSubject();
            int count = 0;

            var sub = subject.Subscribe(() => count++);

            subject.Invoke();
            NAssert.AreEqual(1, count);

            sub.Dispose();
            subject.Invoke();
            NAssert.AreEqual(1, count, "Disposed observer should not be invoked");
        }
    }
}
