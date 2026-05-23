using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Trecs.Internal
{
    // Per-entity slot header in the PerGroupAddBags storage for the Burst-friendly
    // AddEntity fast path. Sits at offset 0 of every slot; the per-template
    // component bytes follow immediately after at offset sizeof(FastAddSlotHeader).
    //
    // SetMask is a per-slot 256-bit bitfield where bit i is set iff the user
    // wrote component i (in TemplateLayoutHeader.FirstComponentIndex+i order)
    // via the returned NativeEntityInitializer's .Set<T>. The drain pipeline
    // branches on this mask to decide whether to copy from the slot bytes or
    // fall back to the template's default-bytes prototype.
    [EditorBrowsable(EditorBrowsableState.Never)]
    [StructLayout(LayoutKind.Sequential)]
    internal struct FastAddSlotHeader
    {
        public EntityHandle ReservedRef; // 8 bytes
        public uint SortKey; // 4 bytes
        public int AccessorId; // 4 bytes
        public TemplateComponentMask SetMask; // 32 bytes
        // Total: 48 bytes
    }
}
