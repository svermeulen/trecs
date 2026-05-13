using UnityEngine;

namespace Trecs.Samples.Interpolation
{
    /// <summary>
    /// Variable-update renderer: demonstrates the key difference between
    /// interpolated and raw position reading.
    ///
    /// Smooth entities: reads Interpolated&lt;Position&gt; — the value is
    /// blended between previous and current fixed-frame positions based on
    /// how far through the current fixed timestep we are. Result: fluid motion.
    ///
    /// Raw entities: reads Position directly — the value only changes at
    /// fixed-update boundaries (10 Hz). Between fixed frames, the position
    /// stays constant, causing visible stutter when rendering at 60+ fps.
    /// </summary>
    [ExecuteIn(SystemPhase.Presentation)]
    public partial class OrbitPresenter : ISystem
    {
        readonly RenderableGameObjectManager _goManager;

        public OrbitPresenter(RenderableGameObjectManager goManager)
        {
            _goManager = goManager;
        }

        public void Execute()
        {
            RenderSmooth();
            RenderRaw();
        }

        // Interpolated<Position> is only present on entities whose
        // template marked Position with [Interpolated].
        [ForEachEntity(typeof(OrbitTags.Smooth))]
        void RenderSmooth(in SmoothOrbitView view)
        {
            var go = _goManager.Resolve(view.GameObjectId);
            go.transform.position = (Vector3)view.InterpolatedPosition;
            go.transform.rotation = view.InterpolatedRotation;
        }

        // Raw entities only have Position and Rotation (no Interpolated wrappers).
        // Reading these in variable update shows the fixed-frame values,
        // which "jump" at each fixed timestep boundary.
        [ForEachEntity(typeof(OrbitTags.Raw))]
        void RenderRaw(in RawOrbitView view)
        {
            var go = _goManager.Resolve(view.GameObjectId);
            go.transform.position = (Vector3)view.Position;
            go.transform.rotation = view.Rotation;
        }

        partial struct SmoothOrbitView
            : IAspect,
                IRead<Interpolated<Position>, Interpolated<Rotation>, GameObjectId> { }

        partial struct RawOrbitView : IAspect, IRead<Position, Rotation, GameObjectId> { }
    }
}
