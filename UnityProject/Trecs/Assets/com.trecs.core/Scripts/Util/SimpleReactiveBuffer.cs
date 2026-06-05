using System;
using System.Collections.Generic;

namespace Trecs.Internal
{
    public sealed class SimpleReactiveBuffer
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

            try
            {
                foreach (var action in actionsToFlush)
                {
                    action();
                }
            }
            finally
            {
                // Reset even if an action throws — otherwise _isFlushing stays stuck
                // true and every subsequent AddAction runs immediately instead of
                // buffering, silently defeating the buffer for the object's lifetime.
                _isFlushing = false;
            }
        }
    }
}
