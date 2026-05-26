using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Trecs.Collections;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Internal
{
    // One entry per component on a template, in the same order as
    // ResolvedTemplate.ComponentDeclarations. ByteOffset is the running offset within
    // the per-entity slot used by the Burst-friendly AddEntity fast path; ByteSize is
    // UnsafeUtility.SizeOf<T>(). TypeIdValue is the ComponentTypeId for the
    // component (stored as int for Burst-friendliness).
    internal struct NativeComponentLayoutEntry
    {
        public int TypeIdValue;
        public int ByteOffset;
        public int ByteSize;
    }

    // One per group (indexed by GroupIndex.Index). Points into the flat
    // _componentEntries and _defaultBytes arrays. ZeroDefaultMask has bit i set when
    // the component at position FirstComponentIndex+i has all-zero default bytes —
    // lets the parallel-fill pass MemClear the destination instead of MemCpy'ing
    // the prototype default bytes.
    internal struct NativeTemplateLayoutHeader
    {
        public int FirstComponentIndex;
        public int ComponentCount;
        public int TotalEntityBytes;
        public int DefaultBytesOffset;
        public TemplateComponentMask ZeroDefaultMask;
    }

    // Built once at WorldInfo construction. Captures every group's per-entity slot
    // layout so the Burst-friendly AddEntity fast path can: (a) resolve "what's the
    // slot size for this group" via the header, (b) resolve "where does component X
    // go in the slot" via a linear scan of the entries, (c) memcpy default bytes for
    // a fresh slot without bouncing through managed code.
    //
    // Read-only for the world's lifetime; consumers are expected to read by value.
    internal sealed class WorldComponentLayouts : IDisposable
    {
        // Hard cap on components per template. ZeroDefaultMask + FastAddSlotHeader.SetMask
        // are both TemplateComponentMask (4 × ulong = 256 bits), so component index `ci`
        // is routed through `mask.Set(ci)` / `mask.IsSet(ci)` — the encoding breaks for
        // ci >= 256. Asserted at construction so pathological templates fail loud at
        // world init rather than corrupting masks silently at runtime.
        public const int MaxComponentsPerTemplate = 256;

        NativeArray<NativeTemplateLayoutHeader> _headers;
        NativeArray<NativeComponentLayoutEntry> _entries;
        NativeArray<byte> _defaultBytes;

        // (groupIndex << 32) | (uint)typeIdValue -> component-slot index `ci` within
        // that group's template layout. Used by NativeEntityInitializer.Set<T> to
        // resolve a TypeId to its slot in O(1) instead of linear-scanning the
        // group's entries — critical when a single AddEntity Set's all (or many)
        // components on a wide template (the linear-scan cost was O(N) per .Set,
        // so O(N^2) for "set everything").
        UnsafeHashMap<long, int> _typeIdToCi;
        bool _isDisposed;

        // Cached UnsafeUtility.SizeOf<T>() per Type. Instance-scoped so the entries
        // are released alongside the world rather than living forever in a static.
        readonly Dictionary<Type, int> _sizeCache = new();

        public NativeArray<NativeTemplateLayoutHeader> Headers => _headers;
        public NativeArray<NativeComponentLayoutEntry> Entries => _entries;
        public NativeArray<byte> DefaultBytes => _defaultBytes;
        public UnsafeHashMap<long, int> TypeIdToCi => _typeIdToCi;

        public WorldComponentLayouts(ReadOnlyList<GroupIndex> allGroups, WorldInfo worldInfo)
        {
            // Two-pass build: pass 1 counts entries and bytes to size the arrays;
            // pass 2 fills them. Avoids per-group resize churn.
            int totalEntries = 0;
            int totalDefaultBytes = 0;
            int groupCount = allGroups.Count;
            for (int gi = 0; gi < groupCount; gi++)
            {
                var template = worldInfo.GetResolvedTemplateForGroup(allGroups[gi]);
                var decls = template.ComponentDeclarations;
                TrecsDebugAssert.That(
                    decls.Count <= MaxComponentsPerTemplate,
                    "Template '{0}' has {1} components, exceeding the per-template cap of {2}. "
                        + "Templates wider than this overflow TemplateComponentMask's 4 × 64-bit words. "
                        + "Raise MaxComponentsPerTemplate and widen TemplateComponentMask together if more are needed.",
                    template.DebugName,
                    decls.Count,
                    MaxComponentsPerTemplate
                );
                totalEntries += decls.Count;
                for (int ci = 0; ci < decls.Count; ci++)
                {
                    totalDefaultBytes += GetUnmanagedSize(decls[ci].ComponentType);
                }
            }

            _headers = new NativeArray<NativeTemplateLayoutHeader>(
                groupCount,
                Allocator.Persistent
            );
            _entries = new NativeArray<NativeComponentLayoutEntry>(
                totalEntries,
                Allocator.Persistent
            );
            _defaultBytes = new NativeArray<byte>(
                totalDefaultBytes,
                Allocator.Persistent,
                NativeArrayOptions.ClearMemory
            );
            // Capacity = totalEntries: every (group, typeId) pair gets exactly one entry.
            _typeIdToCi = new UnsafeHashMap<long, int>(totalEntries, Allocator.Persistent);

            int entryCursor = 0;
            int byteCursor = 0;
            unsafe
            {
                byte* defaultBytesBase = (byte*)_defaultBytes.GetUnsafePtr();
                for (int gi = 0; gi < groupCount; gi++)
                {
                    var template = worldInfo.GetResolvedTemplateForGroup(allGroups[gi]);
                    var decls = template.ComponentDeclarations;
                    int slotOffset = 0;
                    var zeroMask = default(TemplateComponentMask);
                    int headerFirstEntry = entryCursor;
                    int headerDefaultBytesOffset = byteCursor;

                    for (int ci = 0; ci < decls.Count; ci++)
                    {
                        var decl = decls[ci];
                        int size = GetUnmanagedSize(decl.ComponentType);
                        int typeIdValue = TypeIdForComponent(decl.ComponentType).Value;
                        _entries[entryCursor++] = new NativeComponentLayoutEntry
                        {
                            TypeIdValue = typeIdValue,
                            ByteOffset = slotOffset,
                            ByteSize = size,
                        };
                        _typeIdToCi.Add(MakeKey(gi, typeIdValue), ci);
                        bool isZeroDefault = WriteDefaultBytesAndCheckZero(
                            decl,
                            defaultBytesBase + byteCursor,
                            size
                        );
                        if (isZeroDefault)
                        {
                            zeroMask.Set(ci);
                        }
                        slotOffset += size;
                        byteCursor += size;
                    }

                    _headers[gi] = new NativeTemplateLayoutHeader
                    {
                        FirstComponentIndex = headerFirstEntry,
                        ComponentCount = decls.Count,
                        TotalEntityBytes = slotOffset,
                        DefaultBytesOffset = headerDefaultBytesOffset,
                        ZeroDefaultMask = zeroMask,
                    };
                }
            }
        }

        // Composite key for _typeIdToCi: high 32 bits = group index, low 32 bits =
        // TypeId value. Cast through uint masks any sign bit on negative TypeIds so
        // the upper half is exclusively the group's domain. Burst-side
        // NativeEntityInitializer.Set<T> uses an inlined copy of this formula.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long MakeKey(int groupIndex, int typeIdValue) =>
            ((long)groupIndex << 32) | (uint)typeIdValue;

        // Resolve TypeId for a component type via the registered ComponentTypeId<T>.Value path.
        // Uses reflection — only called at world build time, never on the hot path.
        static TypeId TypeIdForComponent(Type t)
        {
            var typeIdGeneric = typeof(TypeId<>).MakeGenericType(t);
            var valueProp = typeIdGeneric.GetProperty(
                "Value",
                BindingFlags.Public | BindingFlags.Static
            );
            return (TypeId)valueProp.GetValue(null);
        }

        // UnsafeUtility.SizeOf<T>() via reflection — every component is `where T : unmanaged`
        // so this is exact. Cached to avoid repeating reflection across same-template groups.
        int GetUnmanagedSize(Type t)
        {
            if (_sizeCache.TryGetValue(t, out var cached))
            {
                return cached;
            }
            var method = typeof(UnsafeUtility).GetMethod(
                nameof(UnsafeUtility.SizeOf),
                1,
                BindingFlags.Public | BindingFlags.Static,
                null,
                Type.EmptyTypes,
                null
            );
            int size = (int)method.MakeGenericMethod(t).Invoke(null, null);
            _sizeCache[t] = size;
            return size;
        }

        // Writes the component's default bytes (boxed user-provided default, or zero-fill
        // if no default is set) to dest. Returns true iff the resulting bytes are all zero.
        // The destination buffer is pre-zeroed at allocation time, so no-default falls
        // through naturally and the zero-bytes branch just reports true.
        static unsafe bool WriteDefaultBytesAndCheckZero(
            IResolvedComponentDeclaration decl,
            byte* dest,
            int size
        )
        {
            var boxedDefault = decl.TryGetDefaultValue();
            if (boxedDefault == null)
            {
                // Buffer was zeroed at allocation — no copy needed, and the result is
                // by construction all-zero.
                return true;
            }
            var handle = GCHandle.Alloc(boxedDefault, GCHandleType.Pinned);
            try
            {
                var srcPtr = (void*)handle.AddrOfPinnedObject();
                UnsafeUtility.MemCpy(dest, srcPtr, size);
            }
            finally
            {
                handle.Free();
            }
            bool allZero = true;
            for (int i = 0; i < size; i++)
            {
                if (dest[i] != 0)
                {
                    allZero = false;
                    break;
                }
            }
            return allZero;
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }
            if (_headers.IsCreated)
            {
                _headers.Dispose();
            }
            if (_entries.IsCreated)
            {
                _entries.Dispose();
            }
            if (_defaultBytes.IsCreated)
            {
                _defaultBytes.Dispose();
            }
            if (_typeIdToCi.IsCreated)
            {
                _typeIdToCi.Dispose();
            }
            _isDisposed = true;
        }
    }
}
