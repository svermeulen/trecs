using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    // Builder returned by NativeWorldAccessor.AddEntity. Writes each .Set<T>
    // component into a fixed offset within the entity's pre-reserved slot in the
    // PerGroupAddBags cell — no variable-length tail, no per-component TypeId tag.
    //
    // The slot's `setMask` (TemplateComponentMask, 256-bit) tracks which
    // component slots the user wrote explicitly. The drain pipeline branches
    // per component:
    //   - If the corresponding bit is set in setMask → MemCpy from this slot.
    //   - Else, if the component's bit is set in the template's ZeroDefaultMask
    //     → MemClear in the destination.
    //   - Else → MemCpy from the template's default-bytes prototype.
    //
    // The struct stores raw pointers because it's a ref-struct returned by a
    // Burst-compiled job-safe method; bounds-checked NativeArray indexer access
    // would force the indexer through its safety handle on every .Set call.
    public readonly unsafe ref struct NativeEntityInitializer
    {
        readonly byte* _slotComponentBytes;
        readonly TemplateComponentMask* _setMaskPtr;
        readonly NativeComponentLayoutEntry* _layoutEntriesBase;
        readonly int _firstEntryIndex;
        readonly UnsafeHashMap<long, int> _typeIdToCi;
        readonly long _groupKeyPrefix;
        readonly EntityHandle _handle;

        internal NativeEntityInitializer(
            byte* slotComponentBytes,
            TemplateComponentMask* setMaskPtr,
            NativeComponentLayoutEntry* layoutEntriesBase,
            int firstEntryIndex,
            UnsafeHashMap<long, int> typeIdToCi,
            long groupKeyPrefix,
            EntityHandle handle
        )
        {
            _slotComponentBytes = slotComponentBytes;
            _setMaskPtr = setMaskPtr;
            _layoutEntriesBase = layoutEntriesBase;
            _firstEntryIndex = firstEntryIndex;
            _typeIdToCi = typeIdToCi;
            _groupKeyPrefix = groupKeyPrefix;
            _handle = handle;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NativeEntityInitializer Set<T>(in T component)
            where T : unmanaged, IEntityComponent
        {
            int typeIdValue = TypeId<T>.Value.Value;
            // Composite key mirrors WorldComponentLayouts.MakeKey. Inlined here
            // so the hot path doesn't reach across files.
            long key = _groupKeyPrefix | (uint)typeIdValue;
            if (!_typeIdToCi.TryGetValue(key, out int ci))
            {
                throw new TrecsException(
                    "Component type not found in template layout for AddEntity"
                );
            }
            var entry = _layoutEntriesBase[_firstEntryIndex + ci];
            *(T*)(_slotComponentBytes + entry.ByteOffset) = component;
            TemplateComponentMask.SetUnsafe(_setMaskPtr, ci);
            return this;
        }

        public EntityHandle Handle => _handle;
    }
}
