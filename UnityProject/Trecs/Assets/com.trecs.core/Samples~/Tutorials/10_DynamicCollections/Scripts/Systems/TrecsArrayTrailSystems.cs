using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.DynamicCollections
{
    /// <summary>
    /// Pushes each character's new position into its
    /// <see cref="TrecsArray{T}"/> ring buffer once the character has moved
    /// more than <c>SampleSettings.TrailMinSampleDistance</c> from the
    /// previous sample. Same ring-buffer shape as
    /// <see cref="FixedArrayTrailUpdater"/>: writes to the slot just past
    /// the live range, then either grows <c>Count</c> (buffer not yet full)
    /// or advances <c>Head</c> (just overwrote the oldest entry). The
    /// difference vs. FixedArray is that the storage lives on the world's
    /// shared native chunk store — the length was chosen at
    /// <c>TrecsArray.Alloc</c> time (see <see cref="SceneLifecycle"/>) and
    /// isn't baked into the component's type, so changing
    /// <c>SampleSettings.TrailLength</c> in the inspector resizes the
    /// backing buffer for every new entity.
    /// </summary>
    [ExecuteAfter(typeof(CharacterMover))]
    public partial class TrecsArrayTrailUpdater : ISystem
    {
        readonly SampleSettings _settings;

        public TrecsArrayTrailUpdater(SampleSettings settings)
        {
            _settings = settings;
        }

        [ForEachEntity(
            typeof(DynamicCollectionsTags.Character),
            typeof(DynamicCollectionsTags.TrecsArrayTrail)
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

            ref var trail = ref character.TrailTrecsArray;
            var write = trail.Positions.Write(World);
            int capacity = write.Length;
            // A zero-length array degenerates to a no-op trail — skip the
            // modulo to avoid a divide-by-zero. The visible trail in this
            // edge case is just the live position pushed by the presenter.
            if (capacity == 0)
            {
                character.LastSamplePosition = character.Position;
                return;
            }

            int writeIndex = (trail.Head + trail.Count) % capacity;
            write[writeIndex] = character.Position;

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
                IWrite<TrailTrecsArray, LastSamplePosition> { }
    }

    /// <summary>
    /// Variable-update renderer for TrecsArray-backed trails. Walks the
    /// ring buffer from oldest to newest and pushes each position to the
    /// entity's <see cref="LineRenderer"/>, then extends the line to the
    /// live character position so the visible tail tracks the sphere even
    /// when the updater is sampling sparsely.
    /// </summary>
    [ExecuteIn(SystemPhase.Presentation)]
    public partial class TrecsArrayTrailPresenter : ISystem
    {
        readonly RenderableGameObjectManager _goManager;

        public TrecsArrayTrailPresenter(RenderableGameObjectManager goManager)
        {
            _goManager = goManager;
        }

        [ForEachEntity(
            typeof(DynamicCollectionsTags.Character),
            typeof(DynamicCollectionsTags.TrecsArrayTrail)
        )]
        void Execute(in Character character)
        {
            var go = _goManager.Resolve(character.GameObjectId);
            go.transform.position = (Vector3)character.Position;

            ref readonly var trail = ref character.TrailTrecsArray;
            var read = trail.Positions.Read(World);
            var lineRenderer = go.GetComponent<LineRenderer>();
            lineRenderer.positionCount = trail.Count + 1;

            int capacity = read.Length;
            for (int i = 0; i < trail.Count; i++)
            {
                lineRenderer.SetPosition(i, (Vector3)read[(trail.Head + i) % capacity]);
            }
            lineRenderer.SetPosition(trail.Count, (Vector3)character.Position);
        }

        partial struct Character : IAspect, IRead<Position, TrailTrecsArray, GameObjectId> { }
    }
}
