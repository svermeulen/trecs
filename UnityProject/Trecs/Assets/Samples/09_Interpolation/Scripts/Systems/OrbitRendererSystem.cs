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
    [VariableUpdate]
    public partial class OrbitRendererSystem : ISystem
    {
        readonly GameObjectRegistry _registry;

        public OrbitRendererSystem(GameObjectRegistry registry)
        {
            _registry = registry;
        }

        public void Execute()
        {
            RenderSmooth();
            RenderRaw();
        }

        // Interpolated<Position> is only present on entities whose
        // template marked Position with [Interpolated].
        [ForEachEntity(Tag = typeof(OrbitTags.Smooth))]
        void RenderSmooth(in SmoothOrbitView view)
        {
            var go = _registry.Resolve(view.GameObjectId);
            go.transform.position = (Vector3)view.InterpolatedPosition;
        }

        // Raw entities only have Position (no Interpolated wrapper).
        // Reading Position in variable update shows the fixed-frame
        // position, which "jumps" at each fixed timestep boundary.
        [ForEachEntity(Tag = typeof(OrbitTags.Raw))]
        void RenderRaw(in RawOrbitView view)
        {
            var go = _registry.Resolve(view.GameObjectId);
            go.transform.position = (Vector3)view.Position;
        }

        partial struct SmoothOrbitView : IAspect, IRead<Interpolated<Position>, GameObjectId> { }

        partial struct RawOrbitView : IAspect, IRead<Position, GameObjectId> { }
    }
}
