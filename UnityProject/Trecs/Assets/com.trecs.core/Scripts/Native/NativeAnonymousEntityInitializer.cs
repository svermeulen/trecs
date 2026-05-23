using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    // Fire-and-forget counterpart to NativeEntityInitializer — returned by
    // the AddEntity overloads that don't take a pre-reserved EntityHandle.
    // No .Handle property: at AddEntity time the handle hasn't been claimed
    // yet (the slot header carries EntityHandle.Null until
    // ClaimDeferredHandlesForNativeAdds runs post-sort during the drain).
    // Otherwise identical in shape and .Set<T> behaviour to the pre-reserved
    // variant.
    public readonly unsafe ref struct NativeAnonymousEntityInitializer
    {
        readonly byte* _slotComponentBytes;
        readonly TemplateComponentMask* _setMaskPtr;
        readonly NativeComponentLayoutEntry* _layoutEntriesBase;
        readonly int _firstEntryIndex;
        readonly UnsafeHashMap<long, int> _typeIdToCi;
        readonly long _groupKeyPrefix;

        internal NativeAnonymousEntityInitializer(
            byte* slotComponentBytes,
            TemplateComponentMask* setMaskPtr,
            NativeComponentLayoutEntry* layoutEntriesBase,
            int firstEntryIndex,
            UnsafeHashMap<long, int> typeIdToCi,
            long groupKeyPrefix
        )
        {
            _slotComponentBytes = slotComponentBytes;
            _setMaskPtr = setMaskPtr;
            _layoutEntriesBase = layoutEntriesBase;
            _firstEntryIndex = firstEntryIndex;
            _typeIdToCi = typeIdToCi;
            _groupKeyPrefix = groupKeyPrefix;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NativeAnonymousEntityInitializer Set<T>(in T component)
            where T : unmanaged, IEntityComponent
        {
            int typeIdValue = TypeId<T>.Value.Value;
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
    }
}
