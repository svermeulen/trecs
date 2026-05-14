namespace Trecs.Samples.Snake
{
    /// <summary>
    /// Trims the oldest segments until the segment count matches the
    /// snake's target body length (SnakeLength.Value - 1, since length
    /// counts the head). Sorts segments by SegmentAge.FrameSpawned each
    /// frame so age order survives snapshot loads cleanly.
    /// </summary>
    [ExecuteAfter(typeof(FoodConsumeSystem))]
    public partial class SegmentTrimSystem : ISystem
    {
        public void Execute()
        {
            int targetSegmentCount = World.GlobalComponent<SnakeLength>().Read.Value - 1;
            int currentCount = World.CountEntitiesWithTags<SnakeTags.SnakeSegment>();

            if (currentCount <= targetSegmentCount)
            {
                return;
            }

            int? oldestAge = null;
            EntityIndex? oldestSegment = null;

            foreach (var segment in SnakeSegment.Query(World).WithTags<SnakeTags.SnakeSegment>())
            {
                if (!oldestAge.HasValue || segment.SegmentAge < oldestAge.Value)
                {
                    oldestAge = segment.SegmentAge;
                    oldestSegment = segment.EntityIndex;
                }
            }

            World.RemoveEntity(oldestSegment.Value);
        }

        partial struct SnakeSegment : IAspect, IRead<SegmentAge> { }
    }
}
