using System;
using System.Collections.Generic;
using UnityEngine;

namespace Trecs.Samples
{
    public class Bootstrap : MonoBehaviour
    {
        public CompositionRootBase CompositionRoot;

        List<Action> _disposables;
        List<Action> _tickables;
        List<Action> _lateTickables;

        void Awake()
        {
            Application.targetFrameRate = 2000;

            CompositionRoot.Construct(
                out var initializables,
                out _tickables,
                out _lateTickables,
                out _disposables
            );

            foreach (var initializable in initializables)
            {
                initializable();
            }
        }

        void Update()
        {
            foreach (var tickable in _tickables)
            {
                tickable();
            }
        }

        void LateUpdate()
        {
            foreach (var tickable in _lateTickables)
            {
                tickable();
            }
        }

        void OnDestroy()
        {
            foreach (var disposables in _disposables)
            {
                disposables();
            }
        }
    }
}
