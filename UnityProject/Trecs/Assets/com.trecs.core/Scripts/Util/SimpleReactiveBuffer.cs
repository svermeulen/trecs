using System;
using System.Collections.Generic;

namespace Trecs.Internal
{
    public class SimpleReactiveBuffer
    {
        readonly List<Action> _bufferedActions = new();
        bool _isFlushing;

        public SimpleReactiveBuffer() { }

        internal void AddAction(Action action)
        {
            if (_isFlushing)
            {
                action();
            }
            else
            {
                _bufferedActions.Add(action);
            }
        }

        public void Flush()
        {
            if (_bufferedActions.Count == 0)
            {
                return;
            }

            _isFlushing = true;

            var actionsToFlush = new List<Action>(_bufferedActions);
            _bufferedActions.Clear();

            foreach (var action in actionsToFlush)
            {
                action();
            }

            _isFlushing = false;
        }
    }
}
