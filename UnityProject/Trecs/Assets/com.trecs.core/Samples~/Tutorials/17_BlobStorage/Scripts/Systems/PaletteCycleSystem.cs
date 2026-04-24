using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.BlobStorage
{
    /// <summary>
    /// Samples each entity's referenced palette over time and writes the
    /// result into <see cref="ColorComponent"/>. Demonstrates how a system
    /// consumes a shared blob: <c>palette.Value.Get(World.Heap)</c> resolves
    /// the handle through the world's heap accessor.
    /// </summary>
    public partial class PaletteCycleSystem : ISystem
    {
        [ForEachEntity(Tag = typeof(SampleTags.Swatch))]
        void Execute(in PaletteRef palette, ref ColorComponent color)
        {
            var table = palette.Value.Get(World.Heap);
            if (table.Colors.Count == 0)
            {
                return;
            }

            float t = World.ElapsedTime * palette.CycleSpeed;
            float f = math.frac(t) * table.Colors.Count;
            int lo = (int)f % table.Colors.Count;
            int hi = (lo + 1) % table.Colors.Count;
            float blend = f - lo;

            color.Value = Color.Lerp(table.Colors[lo], table.Colors[hi], blend);
        }
    }
}
