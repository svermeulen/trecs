using System;
using System.Collections.Generic;

namespace Trecs.Internal
{
    // Here we implement a super lightweight version of reactive x observerables
    // Where there are no streams or LINQ and instead all we retain is the concept
    // of subscriptions as disposables

    public sealed class SimpleSubject : ISimpleObservable
    {
        readonly List<Action> _observers = new();
        readonly List<int> _priorities = new();
        readonly List<Action> _removeQueue = new();

        bool _isInvoking;

        public int NumObservers
        {
            get { return _observers.Count; }
        }

        public IDisposable Subscribe(Action handler)
        {
            return Subscribe(handler, 0);
        }

        public IDisposable Subscribe(Action handler, int priority)
        {
            TrecsAssert.That(!_isInvoking, "Cannot subscribe during invocation");
            int index = FindInsertionIndex(priority);
            _observers.Insert(index, handler);
            _priorities.Insert(index, priority);
            return new ActionDisposable(() => Unsubscribe(handler));
        }

        public IDisposable Subscribe(SimpleReactiveBuffer buffer, Action handler)
        {
            return Subscribe(buffer, handler, 0);
        }

        public IDisposable Subscribe(SimpleReactiveBuffer buffer, Action handler, int priority)
        {
            TrecsAssert.That(!_isInvoking, "Cannot subscribe during invocation");
            Action bufferedHandler = () => buffer.AddAction(handler);
            int index = FindInsertionIndex(priority);
            _observers.Insert(index, bufferedHandler);
            _priorities.Insert(index, priority);
            return new ActionDisposable(() => Unsubscribe(bufferedHandler));
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

        void Unsubscribe(Action handler)
        {
            if (_isInvoking)
            {
                _removeQueue.Add(handler);
            }
            else
            {
                TrecsAssert.That(_removeQueue.Count == 0);
                int index = _observers.IndexOf(handler);
                if (index >= 0)
                {
                    _observers.RemoveAt(index);
                    _priorities.RemoveAt(index);
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
                    foreach (var observer in _removeQueue)
                    {
                        int index = _observers.IndexOf(observer);
                        if (index >= 0)
                        {
                            _observers.RemoveAt(index);
                            _priorities.RemoveAt(index);
                        }
                    }

                    _removeQueue.Clear();
                }
            }
        }
    }

    public sealed class SimpleSubject<T1> : ISimpleObservable<T1>
    {
        readonly List<Action<T1>> _observers = new();
        readonly List<int> _priorities = new();
        readonly List<Action<T1>> _removeQueue = new();

        bool _isInvoking;

        public int NumObservers
        {
            get { return _observers.Count; }
        }

        public IDisposable Subscribe(Action<T1> handler)
        {
            return Subscribe(handler, 0);
        }

        public IDisposable Subscribe(Action<T1> handler, int priority)
        {
            TrecsAssert.That(!_isInvoking, "Cannot subscribe during invocation");
            int index = FindInsertionIndex(priority);
            _observers.Insert(index, handler);
            _priorities.Insert(index, priority);
            return new ActionDisposable(() => Unsubscribe(handler));
        }

        public IDisposable Subscribe(SimpleReactiveBuffer buffer, Action<T1> handler)
        {
            return Subscribe(buffer, handler, 0);
        }

        public IDisposable Subscribe(SimpleReactiveBuffer buffer, Action<T1> handler, int priority)
        {
            TrecsAssert.That(!_isInvoking, "Cannot subscribe during invocation");
            Action<T1> bufferedHandler = arg1 => buffer.AddAction(() => handler(arg1));
            int index = FindInsertionIndex(priority);
            _observers.Insert(index, bufferedHandler);
            _priorities.Insert(index, priority);
            return new ActionDisposable(() => Unsubscribe(bufferedHandler));
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

        void Unsubscribe(Action<T1> handler)
        {
            if (_isInvoking)
            {
                _removeQueue.Add(handler);
            }
            else
            {
                TrecsAssert.That(_removeQueue.Count == 0);
                int index = _observers.IndexOf(handler);
                if (index >= 0)
                {
                    _observers.RemoveAt(index);
                    _priorities.RemoveAt(index);
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
                    foreach (var observer in _removeQueue)
                    {
                        int index = _observers.IndexOf(observer);
                        if (index >= 0)
                        {
                            _observers.RemoveAt(index);
                            _priorities.RemoveAt(index);
                        }
                    }

                    _removeQueue.Clear();
                }
            }
        }
    }

    public sealed class SimpleSubject<T1, T2> : ISimpleObservable<T1, T2>
    {
        readonly List<Action<T1, T2>> _observers = new();
        readonly List<int> _priorities = new();
        readonly List<Action<T1, T2>> _removeQueue = new();

        bool _isInvoking;

        public int NumObservers
        {
            get { return _observers.Count; }
        }

        public IDisposable Subscribe(Action<T1, T2> handler)
        {
            return Subscribe(handler, 0);
        }

        public IDisposable Subscribe(Action<T1, T2> handler, int priority)
        {
            TrecsAssert.That(!_isInvoking, "Cannot subscribe during invocation");
            int index = FindInsertionIndex(priority);
            _observers.Insert(index, handler);
            _priorities.Insert(index, priority);
            return new ActionDisposable(() => Unsubscribe(handler));
        }

        public IDisposable Subscribe(SimpleReactiveBuffer buffer, Action<T1, T2> handler)
        {
            return Subscribe(buffer, handler, 0);
        }

        public IDisposable Subscribe(
            SimpleReactiveBuffer buffer,
            Action<T1, T2> handler,
            int priority
        )
        {
            TrecsAssert.That(!_isInvoking, "Cannot subscribe during invocation");
            Action<T1, T2> bufferedHandler = (arg1, arg2) =>
                buffer.AddAction(() => handler(arg1, arg2));
            int index = FindInsertionIndex(priority);
            _observers.Insert(index, bufferedHandler);
            _priorities.Insert(index, priority);
            return new ActionDisposable(() => Unsubscribe(bufferedHandler));
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

        void Unsubscribe(Action<T1, T2> handler)
        {
            if (_isInvoking)
            {
                _removeQueue.Add(handler);
            }
            else
            {
                TrecsAssert.That(_removeQueue.Count == 0);
                int index = _observers.IndexOf(handler);
                if (index >= 0)
                {
                    _observers.RemoveAt(index);
                    _priorities.RemoveAt(index);
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
                    foreach (var observer in _removeQueue)
                    {
                        int index = _observers.IndexOf(observer);
                        if (index >= 0)
                        {
                            _observers.RemoveAt(index);
                            _priorities.RemoveAt(index);
                        }
                    }

                    _removeQueue.Clear();
                }
            }
        }
    }
}
