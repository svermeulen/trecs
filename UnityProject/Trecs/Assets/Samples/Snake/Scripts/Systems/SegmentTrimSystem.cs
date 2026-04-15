using System.Collections.Generic;

namespace Trecs.Samples.Snake
{
    /// <summary>
    /// Trims the oldest segments until the segment count matches the
    /// snake's target body length (SnakeLength.Value - 1, since length
    /// counts the head). Sorts segments by SegmentAge.FrameSpawned each
    /// frame so age order survives bookmark loads cleanly.
    /// </summary>
    [ExecutesAfter(typeof(FoodConsumeSystem))]
    public partial class SegmentTrimSystem : ISystem
    {
        readonly SnakeGameObjectManager _goManager;

        public SegmentTrimSystem(SnakeGameObjectManager goManager)
        {
            _goManager = goManager;
        }

        public void Execute()
        {
            int targetSegmentCount = World.GlobalComponent<SnakeLength>().Read.Value - 1;
            int currentCount = World.CountEntitiesWithTags<SnakeTags.SnakeSegment>();

            if (currentCount <= targetSegmentCount)
            {
                return;
            }

            int toRemove = currentCount - targetSegmentCount;

            // Sample-scale entity counts (a few dozen segments at most), so
            // a per-frame allocation is acceptable here. Avoids storing
            // member state on a fixed system.
            var snapshot = new List<(int frame, EntityIndex index, GameObjectId goId)>(
                currentCount
            );
            foreach (
                var entityIndex in World.Query().WithTags<SnakeTags.SnakeSegment>().EntityIndices()
            )
            {
                var age = World.Component<SegmentAge>(entityIndex).Read.FrameSpawned;
                var goId = World.Component<GameObjectId>(entityIndex).Read;
                snapshot.Add((age, entityIndex, goId));
            }

            snapshot.Sort((a, b) => a.frame.CompareTo(b.frame));

            for (int i = 0; i < toRemove; i++)
            {
                _goManager.Destroy(snapshot[i].goId);
                World.RemoveEntity(snapshot[i].index);
            }
        }
    }
}
