using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.DynamicCollections
{
    /// <summary>
    /// Appends each character's new position to its
    /// <see cref="TrecsList{T}"/> trail once the character has moved more
    /// than <c>SampleSettings.TrailMinSampleDistance</c> from the previous
    /// sample. Unlike <c>FixedList</c>, the backing buffer lives on the
    /// world's shared native chunk store and grows geometrically — the
    /// managed write wrapper auto-reallocates inside <c>Add</c> when the
    /// list hits capacity.
    /// </summary>
    [ExecuteAfter(typeof(CharacterMover))]
    public partial class TrecsListTrailUpdater : ISystem
    {
        readonly SampleSettings _settings;

        public TrecsListTrailUpdater(SampleSettings settings)
        {
            _settings = settings;
        }

        [ForEachEntity(
            typeof(DynamicCollectionsTags.Character),
            typeof(DynamicCollectionsTags.TrecsListTrail)
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

            // Trecs list is unbounded so can add as much as we want
            // and it will auto resize
            var positions = character.TrailTrecsList.Write(World);

            positions.Add(character.Position);
            character.LastSamplePosition = character.Position;
        }

        partial struct Character
            : IAspect,
                IRead<Position>,
                IWrite<TrailTrecsList, LastSamplePosition> { }
    }

    /// <summary>
    /// Variable-update renderer for TrecsList-backed trails. Opens a read
    /// view, walks the list in insertion order, and pushes each position
    /// to the entity's <see cref="LineRenderer"/>, then extends the line
    /// to the live character position so the visible tail tracks the
    /// sphere even when the updater is sampling sparsely.
    /// </summary>
    [ExecuteIn(SystemPhase.Presentation)]
    public partial class TrecsListTrailPresenter : ISystem
    {
        readonly RenderableGameObjectManager _goManager;

        public TrecsListTrailPresenter(RenderableGameObjectManager goManager)
        {
            _goManager = goManager;
        }

        [ForEachEntity(
            typeof(DynamicCollectionsTags.Character),
            typeof(DynamicCollectionsTags.TrecsListTrail)
        )]
        void Execute(in Character character)
        {
            var go = _goManager.Resolve(character.GameObjectId);
            go.transform.position = (Vector3)character.Position;

            var read = character.TrailTrecsList.Read(World);
            var lineRenderer = go.GetComponent<LineRenderer>();
            lineRenderer.positionCount = read.Count + 1;

            for (int i = 0; i < read.Count; i++)
            {
                lineRenderer.SetPosition(i, (Vector3)read[i]);
            }
            lineRenderer.SetPosition(read.Count, (Vector3)character.Position);
        }

        partial struct Character : IAspect, IRead<Position, TrailTrecsList, GameObjectId> { }
    }
}
