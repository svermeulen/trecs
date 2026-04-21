using Unity.Mathematics;

namespace Trecs.Serialization.Samples.SaveGame
{
    [Unwrap]
    public partial struct GridPos : IEntityComponent
    {
        public int2 Value;
    }

    [Unwrap]
    public partial struct MoveInput : IEntityComponent
    {
        // One-cell step requested this fixed frame: (0,0) = no-op, otherwise a unit vector.
        public int2 Step;
    }
}
