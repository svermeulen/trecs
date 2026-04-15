using System;
using Unity.Jobs;

namespace Trecs
{
    public interface IInterpolatedPreviousSaver
    {
        void Initialize(World world);
        JobHandle Save();

        public Type ComponentType { get; }
    }
}
