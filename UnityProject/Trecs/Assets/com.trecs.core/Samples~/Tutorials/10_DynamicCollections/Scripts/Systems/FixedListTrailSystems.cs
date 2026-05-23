using Trecs.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.DynamicCollections
{
    /// <summary>
    /// Pushes each character's new position into its inline
    /// <see cref="FixedList256{T}"/> ring buffer once the character has
    /// moved more than <c>SampleSettings.TrailMinSampleDistance</c> from
    /// the previous sample. While still filling, appends via
    /// <c>Add</c> and lets the list's <c>Count</c> grow; once full,
    /// overwrites the slot at <c>Head</c> and advances it.
    /// </summary>
    [ExecuteAfter(typeof(CharacterMover))]
    public partial class FixedListTrailUpdater : ISystem
    {
        readonly SampleSettings _settings;

        public FixedListTrailUpdater(SampleSettings settings)
        {
            _settings = settings;
        }

        [ForEachEntity(
            typeof(DynamicCollectionsTags.Character),
            typeof(DynamicCollectionsTags.FixedListTrail)
        )]
        void Execute(in Character character)
        {
            if (
                math.distance(character.Position, character.LastSamplePosition)
                < _settings.TrailMinSampleDistance
            )
            {
                return;
            }

            ref var trail = ref character.TrailFixedList;
            int capacity = trail.Positions.Capacity;

            // FixedList has a fixed upper bound, so let's use
            // a ring buffer shape
            if (trail.Positions.Count < capacity)
            {
                trail.Positions.Add(character.Position);
            }
            else
            {
                trail.Positions.Mut(trail.Head) = character.Position;
                trail.Head = (trail.Head + 1) % capacity;
            }

            character.LastSamplePosition = character.Position;
        }

        partial struct Character
            : IAspect,
                IRead<Position>,
                IWrite<TrailFixedList, LastSamplePosition> { }
    }

    /// <summary>
    /// Variable-update renderer for FixedList-backed trails. Walks the
    /// ring buffer from oldest to newest and pushes each position to the
    /// entity's <see cref="LineRenderer"/>, then extends the line to the
    /// live character position so the visible tail tracks the sphere even
    /// when the updater is sampling sparsely.
    /// </summary>
    [ExecuteIn(SystemPhase.Presentation)]
    public partial class FixedListTrailPresenter : ISystem
    {
        readonly RenderableGameObjectManager _goManager;

        public FixedListTrailPresenter(RenderableGameObjectManager goManager)
        {
            _goManager = goManager;
        }

        [ForEachEntity(
            typeof(DynamicCollectionsTags.Character),
            typeof(DynamicCollectionsTags.FixedListTrail)
        )]
        void Execute(in Character character)
        {
            var go = _goManager.Resolve(character.GameObjectId);
            go.transform.position = (Vector3)character.Position;

            ref readonly var trail = ref character.TrailFixedList;
            var lineRenderer = go.GetComponent<LineRenderer>();
            int count = trail.Positions.Count;
            lineRenderer.positionCount = count + 1;

            int capacity = trail.Positions.Capacity;
            for (int i = 0; i < count; i++)
            {
                lineRenderer.SetPosition(i, (Vector3)trail.Positions[(trail.Head + i) % capacity]);
            }
            lineRenderer.SetPosition(count, (Vector3)character.Position);
        }

        partial struct Character : IAspect, IRead<Position, TrailFixedList, GameObjectId> { }
    }
}
