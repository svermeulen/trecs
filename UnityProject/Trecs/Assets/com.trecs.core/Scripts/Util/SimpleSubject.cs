using System;
using System.Collections.Generic;

namespace Trecs.Internal
{
    // Lightweight observable — subscriptions as disposables, no streams or LINQ

    public sealed class SimpleSubject : ISimpleObservable
    {
        readonly List<Action> _observers = new();
        readonly List<int> _priorities = new();
        readonly List<string> _debugNames = new();
        readonly List<int> _subscriptionIds = new();
        readonly List<int> _removeQueue = new();

        bool _isInvoking;
        int _nextSubscriptionId;

        public int NumObservers
        {
            get { return _observers.Count; }
        }

        public IDisposable Subscribe(Action handler)
        {
            return Subscribe(handler, 0, null);
        }

        public IDisposable Subscribe(Action handler, int priority)
        {
            return Subscribe(handler, priority, null);
        }

        public IDisposable Subscribe(Action handler, int priority, string debugName)
        {
            TrecsDebugAssert.That(!_isInvoking, "Cannot subscribe during invocation");
            int subscriptionId = _nextSubscriptionId++;
            int index = FindInsertionIndex(priority);
            _observers.Insert(index, handler);
            _priorities.Insert(index, priority);
            _debugNames.Insert(index, debugName);
            _subscriptionIds.Insert(index, subscriptionId);
            return new ActionDisposable(() => UnsubscribeById(subscriptionId));
        }

        public IDisposable Subscribe(SimpleReactiveBuffer buffer, Action handler)
        {
            return Subscribe(buffer, handler, 0, null);
        }

        public IDisposable Subscribe(SimpleReactiveBuffer buffer, Action handler, int priority)
        {
            return Subscribe(buffer, handler, priority, null);
        }

        public IDisposable Subscribe(
            SimpleReactiveBuffer buffer,
            Action handler,
            int priority,
            string debugName
        )
        {
            TrecsDebugAssert.That(!_isInvoking, "Cannot subscribe during invocation");
            Action bufferedHandler = () => buffer.AddAction(handler);
            int subscriptionId = _nextSubscriptionId++;
            int index = FindInsertionIndex(priority);
            _observers.Insert(index, bufferedHandler);
            _priorities.Insert(index, priority);
            _debugNames.Insert(index, debugName);
            _subscriptionIds.Insert(index, subscriptionId);
            return new ActionDisposable(() => UnsubscribeById(subscriptionId));
        }

        int FindInsertionIndex(int priority)
        {
            for (int i = 0; i < _priorities.Count; i++)
            {
                if (_priorities[i] > priority)
                    return i;
            }
            return _priorities.Count;
        }

        void UnsubscribeById(int subscriptionId)
        {
            if (_isInvoking)
            {
                _removeQueue.Add(subscriptionId);
            }
            else
            {
                TrecsDebugAssert.That(_removeQueue.Count == 0);
                int index = _subscriptionIds.IndexOf(subscriptionId);
                if (index >= 0)
                {
                    _observers.RemoveAt(index);
                    _priorities.RemoveAt(index);
                    _debugNames.RemoveAt(index);
                    _subscriptionIds.RemoveAt(index);
                }
            }
        }

        public void Invoke()
        {
            _isInvoking = true;
            try
            {
                var numObservers = _observers.Count;
                for (int i = 0; i < numObservers; i++)
                {
                    _observers[i]();
                }
            }
            finally
            {
                _isInvoking = false;

                if (_removeQueue.Count > 0)
                {
                    foreach (var subscriptionId in _removeQueue)
                    {
                        int index = _subscriptionIds.IndexOf(subscriptionId);
                        if (index >= 0)
                        {
                            _observers.RemoveAt(index);
                            _priorities.RemoveAt(index);
                            _debugNames.RemoveAt(index);
                            _subscriptionIds.RemoveAt(index);
                        }
                    }

                    _removeQueue.Clear();
                }
            }
        }

        public string GetDebugNamesSummary()
        {
            var names = new HashSet<string>();
            for (int i = 0; i < _debugNames.Count; i++)
            {
                names.Add(_debugNames[i] ?? "<unknown>");
            }
            return string.Join(", ", names);
        }
    }

    public sealed class SimpleSubject<T1> : ISimpleObservable<T1>
    {
        readonly List<Action<T1>> _observers = new();
        readonly List<int> _priorities = new();
        readonly List<string> _debugNames = new();
        readonly List<int> _subscriptionIds = new();
        readonly List<int> _removeQueue = new();

        bool _isInvoking;
        int _nextSubscriptionId;

        public int NumObservers
        {
            get { return _observers.Count; }
        }

        public IDisposable Subscribe(Action<T1> handler)
        {
            return Subscribe(handler, 0, null);
        }

        public IDisposable Subscribe(Action<T1> handler, int priority)
        {
            return Subscribe(handler, priority, null);
        }

        public IDisposable Subscribe(Action<T1> handler, int priority, string debugName)
        {
            TrecsDebugAssert.That(!_isInvoking, "Cannot subscribe during invocation");
            int subscriptionId = _nextSubscriptionId++;
            int index = FindInsertionIndex(priority);
            _observers.Insert(index, handler);
            _priorities.Insert(index, priority);
            _debugNames.Insert(index, debugName);
            _subscriptionIds.Insert(index, subscriptionId);
            return new ActionDisposable(() => UnsubscribeById(subscriptionId));
        }

        public IDisposable Subscribe(SimpleReactiveBuffer buffer, Action<T1> handler)
        {
            return Subscribe(buffer, handler, 0, null);
        }

        public IDisposable Subscribe(SimpleReactiveBuffer buffer, Action<T1> handler, int priority)
        {
            return Subscribe(buffer, handler, priority, null);
        }

        public IDisposable Subscribe(
            SimpleReactiveBuffer buffer,
            Action<T1> handler,
            int priority,
            string debugName
        )
        {
            TrecsDebugAssert.That(!_isInvoking, "Cannot subscribe during invocation");
            Action<T1> bufferedHandler = arg1 => buffer.AddAction(() => handler(arg1));
            int subscriptionId = _nextSubscriptionId++;
            int index = FindInsertionIndex(priority);
            _observers.Insert(index, bufferedHandler);
            _priorities.Insert(index, priority);
            _debugNames.Insert(index, debugName);
            _subscriptionIds.Insert(index, subscriptionId);
            return new ActionDisposable(() => UnsubscribeById(subscriptionId));
        }

        int FindInsertionIndex(int priority)
        {
            for (int i = 0; i < _priorities.Count; i++)
            {
                if (_priorities[i] > priority)
                    return i;
            }
            return _priorities.Count;
        }

        void UnsubscribeById(int subscriptionId)
        {
            if (_isInvoking)
            {
                _removeQueue.Add(subscriptionId);
            }
            else
            {
                TrecsDebugAssert.That(_removeQueue.Count == 0);
                int index = _subscriptionIds.IndexOf(subscriptionId);
                if (index >= 0)
                {
                    _observers.RemoveAt(index);
                    _priorities.RemoveAt(index);
                    _debugNames.RemoveAt(index);
                    _subscriptionIds.RemoveAt(index);
                }
            }
        }

        public void Invoke(T1 arg1)
        {
            _isInvoking = true;
            try
            {
                var numObservers = _observers.Count;
                for (int i = 0; i < numObservers; i++)
                {
                    _observers[i](arg1);
                }
            }
            finally
            {
                _isInvoking = false;

                if (_removeQueue.Count > 0)
                {
                    foreach (var subscriptionId in _removeQueue)
                    {
                        int index = _subscriptionIds.IndexOf(subscriptionId);
                        if (index >= 0)
                        {
                            _observers.RemoveAt(index);
                            _priorities.RemoveAt(index);
                            _debugNames.RemoveAt(index);
                            _subscriptionIds.RemoveAt(index);
                        }
                    }

                    _removeQueue.Clear();
                }
            }
        }

        public string GetDebugNamesSummary()
        {
            var names = new HashSet<string>();
            for (int i = 0; i < _debugNames.Count; i++)
            {
                names.Add(_debugNames[i] ?? "<unknown>");
            }
            return string.Join(", ", names);
        }
    }

    public sealed class SimpleSubject<T1, T2> : ISimpleObservable<T1, T2>
    {
        readonly List<Action<T1, T2>> _observers = new();
        readonly List<int> _priorities = new();
        readonly List<string> _debugNames = new();
        readonly List<int> _subscriptionIds = new();
        readonly List<int> _removeQueue = new();

        bool _isInvoking;
        int _nextSubscriptionId;

        public int NumObservers
        {
            get { return _observers.Count; }
        }

        public IDisposable Subscribe(Action<T1, T2> handler)
        {
            return Subscribe(handler, 0, null);
        }

        public IDisposable Subscribe(Action<T1, T2> handler, int priority)
        {
            return Subscribe(handler, priority, null);
        }

        public IDisposable Subscribe(Action<T1, T2> handler, int priority, string debugName)
        {
            TrecsDebugAssert.That(!_isInvoking, "Cannot subscribe during invocation");
            int subscriptionId = _nextSubscriptionId++;
            int index = FindInsertionIndex(priority);
            _observers.Insert(index, handler);
            _priorities.Insert(index, priority);
            _debugNames.Insert(index, debugName);
            _subscriptionIds.Insert(index, subscriptionId);
            return new ActionDisposable(() => UnsubscribeById(subscriptionId));
        }

        public IDisposable Subscribe(SimpleReactiveBuffer buffer, Action<T1, T2> handler)
        {
            return Subscribe(buffer, handler, 0, null);
        }

        public IDisposable Subscribe(
            SimpleReactiveBuffer buffer,
            Action<T1, T2> handler,
            int priority
        )
        {
            return Subscribe(buffer, handler, priority, null);
        }

        public IDisposable Subscribe(
            SimpleReactiveBuffer buffer,
            Action<T1, T2> handler,
            int priority,
            string debugName
        )
        {
            TrecsDebugAssert.That(!_isInvoking, "Cannot subscribe during invocation");
            Action<T1, T2> bufferedHandler = (arg1, arg2) =>
                buffer.AddAction(() => handler(arg1, arg2));
            int subscriptionId = _nextSubscriptionId++;
            int index = FindInsertionIndex(priority);
            _observers.Insert(index, bufferedHandler);
            _priorities.Insert(index, priority);
            _debugNames.Insert(index, debugName);
            _subscriptionIds.Insert(index, subscriptionId);
            return new ActionDisposable(() => UnsubscribeById(subscriptionId));
        }

        int FindInsertionIndex(int priority)
        {
            for (int i = 0; i < _priorities.Count; i++)
            {
                if (_priorities[i] > priority)
                    return i;
            }
            return _priorities.Count;
        }

        void UnsubscribeById(int subscriptionId)
        {
            if (_isInvoking)
            {
                _removeQueue.Add(subscriptionId);
            }
            else
            {
                TrecsDebugAssert.That(_removeQueue.Count == 0);
                int index = _subscriptionIds.IndexOf(subscriptionId);
                if (index >= 0)
                {
                    _observers.RemoveAt(index);
                    _priorities.RemoveAt(index);
                    _debugNames.RemoveAt(index);
                    _subscriptionIds.RemoveAt(index);
                }
            }
        }

        public void Invoke(T1 arg1, T2 arg2)
        {
            _isInvoking = true;
            try
            {
                var numObservers = _observers.Count;
                for (int i = 0; i < numObservers; i++)
                {
                    _observers[i](arg1, arg2);
                }
            }
            finally
            {
                _isInvoking = false;

                if (_removeQueue.Count > 0)
                {
                    foreach (var subscriptionId in _removeQueue)
                    {
                        int index = _subscriptionIds.IndexOf(subscriptionId);
                        if (index >= 0)
                        {
                            _observers.RemoveAt(index);
                            _priorities.RemoveAt(index);
                            _debugNames.RemoveAt(index);
                            _subscriptionIds.RemoveAt(index);
                        }
                    }

                    _removeQueue.Clear();
                }
            }
        }

        public string GetDebugNamesSummary()
        {
            var names = new HashSet<string>();
            for (int i = 0; i < _debugNames.Count; i++)
            {
                names.Add(_debugNames[i] ?? "<unknown>");
            }
            return string.Join(", ", names);
        }
    }
}
