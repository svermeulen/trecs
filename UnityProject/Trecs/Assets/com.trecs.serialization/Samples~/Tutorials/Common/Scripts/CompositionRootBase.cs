using System;
using System.Collections.Generic;
using UnityEngine;

namespace Trecs.Serialization.Samples
{
    public abstract class CompositionRootBase : MonoBehaviour
    {
        public abstract void Construct(
            out List<Action> initializables,
            out List<Action> tickables,
            out List<Action> lateTickables,
            out List<Action> disposables
        );
    }
}
