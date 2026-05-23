using Trecs.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.DynamicCollections
{
    /// <summary>
    /// Pushes each character's new position into its inline
    /// <see cref="FixedArray32{T}"/> ring buffer once the character has
    /// moved more than <c>SampleSettings.TrailMinSampleDistance</c> from
    /// the previous sample. Writes to the slot just past the live range,
    /// then either grows <c>Count</c> (buffer not yet full) or advances
    /// <c>Head</c> (just overwrote the oldest entry).
    /// </summary>
    [ExecuteAfter(typeof(CharacterMover))]
    public partial class FixedArrayTrailUpdater : ISystem
    {
        readonly SampleSettings _settings;

        public FixedArrayTrailUpdater(SampleSettings settings)
        {
            _settings = settings;
        }

        [ForEachEntity(
            typeof(DynamicCollectionsTags.Character),
            typeof(DynamicCollectionsTags.FixedArrayTrail)
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

            ref var trail = ref character.TrailFixedArray;
            int capacity = trail.Positions.Length;
            int writeIndex = (trail.Head + trail.Count) % capacity;
            trail.Positions.Mut(writeIndex) = character.Position;

            // Fixed array has a fixed upper bound, so let's use
            // a ring buffer shape
            if (trail.Count < capacity)
            {
                trail.Count++;
            }
            else
            {
                trail.Head = (trail.Head + 1) % capacity;
            }

            character.LastSamplePosition = character.Position;
        }

        partial struct Character
            : IAspect,
                IRead<Position>,
                IWrite<TrailFixedArray, LastSamplePosition> { }
    }

    /// <summary>
    /// Variable-update renderer for FixedArray-backed trails. Walks the
    /// ring buffer from oldest to newest and pushes each position to the
    /// entity's <see cref="LineRenderer"/>, then extends the line to the
    /// live character position so the visible tail tracks the sphere even
    /// when the updater is sampling sparsely.
    /// </summary>
    [ExecuteIn(SystemPhase.Presentation)]
    public partial class FixedArrayTrailPresenter : ISystem
    {
        readonly RenderableGameObjectManager _goManager;

        public FixedArrayTrailPresenter(RenderableGameObjectManager goManager)
        {
            _goManager = goManager;
        }

        [ForEachEntity(
            typeof(DynamicCollectionsTags.Character),
            typeof(DynamicCollectionsTags.FixedArrayTrail)
        )]
        void Execute(in Character character)
        {
            var go = _goManager.Resolve(character.GameObjectId);
            go.transform.position = (Vector3)character.Position;

            ref readonly var trail = ref character.TrailFixedArray;
            var lineRenderer = go.GetComponent<LineRenderer>();
            lineRenderer.positionCount = trail.Count + 1;

            int capacity = trail.Positions.Length;
            for (int i = 0; i < trail.Count; i++)
            {
                lineRenderer.SetPosition(i, (Vector3)trail.Positions[(trail.Head + i) % capacity]);
            }
            lineRenderer.SetPosition(trail.Count, (Vector3)character.Position);
        }

        partial struct Character : IAspect, IRead<Position, TrailFixedArray, GameObjectId> { }
    }
}
