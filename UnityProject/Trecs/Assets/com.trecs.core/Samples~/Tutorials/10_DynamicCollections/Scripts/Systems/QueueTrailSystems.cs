using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.DynamicCollections
{
    /// <summary>
    /// Appends each character's new position to its managed
    /// <see cref="System.Collections.Generic.Queue{T}"/> trail and trims
    /// the queue to <c>SampleSettings.TrailLength</c> whenever the
    /// character has moved more than
    /// <c>SampleSettings.TrailMinSampleDistance</c> from the previous
    /// sample. Reads through the UniquePtr to get the same live Queue
    /// instance every frame.
    /// </summary>
    [ExecuteAfter(typeof(CharacterMover))]
    public partial class QueueTrailUpdater : ISystem
    {
        readonly SampleSettings _settings;

        public QueueTrailUpdater(SampleSettings settings)
        {
            _settings = settings;
        }

        [ForEachEntity(
            typeof(DynamicCollectionsTags.Character),
            typeof(DynamicCollectionsTags.QueueTrail)
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

            // Here we use a managed Queue
            //
            var queue = character.TrailQueue.Get(World);
            queue.Enqueue(character.Position);

            while (queue.Count > _settings.TrailLength)
            {
                queue.Dequeue();
            }

            character.LastSamplePosition = character.Position;
        }

        partial struct Character
            : IAspect,
                IRead<Position, TrailQueue>,
                IWrite<LastSamplePosition> { }
    }

    /// <summary>
    /// Variable-update renderer for queue-backed trails. Walks the live
    /// <see cref="System.Collections.Generic.Queue{T}"/> in insertion order
    /// and pushes each position to the entity's <see cref="LineRenderer"/>,
    /// then extends the line to the live character position so the
    /// visible tail tracks the sphere even when the updater is sampling
    /// sparsely.
    /// </summary>
    [ExecuteIn(SystemPhase.Presentation)]
    public partial class QueueTrailPresenter : ISystem
    {
        readonly RenderableGameObjectManager _goManager;

        public QueueTrailPresenter(RenderableGameObjectManager goManager)
        {
            _goManager = goManager;
        }

        [ForEachEntity(
            typeof(DynamicCollectionsTags.Character),
            typeof(DynamicCollectionsTags.QueueTrail)
        )]
        void Execute(in Character character)
        {
            var go = _goManager.Resolve(character.GameObjectId);
            go.transform.position = (Vector3)character.Position;

            var queue = character.TrailQueue.Get(World);
            var lineRenderer = go.GetComponent<LineRenderer>();
            lineRenderer.positionCount = queue.Count + 1;

            int i = 0;
            foreach (var trailPosition in queue)
            {
                lineRenderer.SetPosition(i++, (Vector3)trailPosition);
            }
            lineRenderer.SetPosition(queue.Count, (Vector3)character.Position);
        }

        partial struct Character : IAspect, IRead<Position, TrailQueue, GameObjectId> { }
    }
}
