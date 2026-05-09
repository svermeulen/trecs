using System;
using System.Collections.Generic;
using System.ComponentModel;
using Unity.Jobs;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class InterpolatedPreviousSaverManager
    {
        readonly List<IInterpolatedPreviousSaver> _savers;

        public InterpolatedPreviousSaverManager(List<IInterpolatedPreviousSaver> savers)
        {
            _savers = savers;
        }

        internal void Initialize(World world)
        {
#if DEBUG
            var registeredTypes = new HashSet<Type>();
            foreach (var saver in _savers)
            {
                if (!registeredTypes.Add(saver.ComponentType))
                {
                    throw new TrecsException(
                        $"Multiple IInterpolatedPreviousSavers registered for component type {saver.ComponentType}"
                    );
                }
            }
#endif
            foreach (var saver in _savers)
            {
                saver.Initialize(world);
            }
        }

        internal void SaveAll()
        {
            var combined = default(JobHandle);

            for (int i = 0; i < _savers.Count; i++)
            {
                combined = JobHandle.CombineDependencies(combined, _savers[i].Save());
            }

            combined.Complete();
        }
    }
}
