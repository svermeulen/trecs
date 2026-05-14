using System;
using Unity.Jobs;

namespace Trecs
{
    /// <summary>
    /// Contract for saving a component's current values into its
    /// <see cref="InterpolatedPrevious{T}"/> counterpart before the next fixed update.
    /// </summary>
    internal interface IInterpolatedPreviousSaver
    {
        void Initialize(World world);
        JobHandle Save();

        public Type ComponentType { get; }
    }
}
