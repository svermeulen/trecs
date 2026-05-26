using System;
using System.Collections.Generic;
using Trecs.Collections;
using Trecs.Internal;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Heap for unmanaged input allocations (<see cref="InputNativeUniquePtr{T}"/>).
    /// Each allocation is an individually-malloc'd buffer registered in a Burst-
    /// readable handle→pointer table; trimming a frame walks the per-frame bucket
    /// and frees each allocation in O(handles-in-frame).
    ///
    /// <para>Public-facing API is via <see cref="InputNativeUniquePtr{T}"/>; this
    /// class is reachable through <see cref="WorldAccessor"/> for the input
    /// pipeline's bookkeeping (allocation, frame-range clear, serialization).</para>
    ///
    /// <para><b>Thread-safety.</b> All mutating entry points (Alloc, ClearAt*,
    /// ClearAll, Dispose, Deserialize) and the main-thread Resolve path are
    /// gated to the main thread. Alloc paths are gated centrally on
    /// <see cref="WorldAccessor.AssertCanAddInputsHeap"/> (which calls
    /// <see cref="WorldAccessor.AssertHeapMainThread"/>); lifecycle and direct-read
    /// paths assert main-thread in-heap because they don't go through
    /// WorldAccessor (lifecycle: EntityInputQueue / EcsHeapAllocator; direct
    /// Resolve: tests and the InputNativeUniquePtr Read(WorldAccessor)
    /// implementation). Burst jobs that read through
    /// <see cref="InputNativeUniqueResolver"/> must not run concurrently with any
    /// mutator on this heap — the underlying <c>NativeIterableDictionary</c> can
    /// rehash on Add and would dangle pointers held by an in-flight job. The
    /// accessor-role system enforces this by construction: Alloc requires an
    /// Input-role accessor (Input phase, no Burst jobs scheduled yet), while
    /// Burst readers run in Fixed phase after submission. The main-thread asserts
    /// catch the structural case (Burst trying to call into the heap directly).
    /// </para>
    ///
    /// <para><b>Type-tag verification.</b> Each <see cref="InputAllocation"/>
    /// carries the <see cref="TypeId"/> the value was allocated as. Both the
    /// main-thread <see cref="ResolveUnsafePtr{T}"/> and the Burst-side
    /// <see cref="InputNativeUniqueResolver.Resolve{T}"/> verify this tag
    /// against <c>TypeId&lt;T&gt;.Value</c>, so a wrong-T read fires in every
    /// build (Burst and managed, release and debug). Tags round-trip through
    /// Serialize/Deserialize, so deserialized handles still get the check.</para>
    /// </summary>
    public sealed class InputNativeUniqueHeap
    {
        readonly TrecsLog _log;

        // Burst-readable handle→(ptr, size, typeHash) table. Burst jobs read
        // through InputNativeUniqueResolver, which wraps this same dictionary
        // by value.
        NativeHashMap<InputPtrHandle, InputAllocation> _allocations;
        InputNativeUniqueResolver _resolver;

        // (frame → handles allocated for that frame). IterableDictionary gives
        // deterministic iteration order for serialize / trim paths. List<>s are
        // pooled to avoid GC churn under high allocation churn.
        readonly IterableDictionary<int, List<InputPtrHandle>> _handlesByFrame = new();
        readonly Stack<List<InputPtrHandle>> _listPool = new();
        readonly List<int> _frameRemoveBuffer = new();

        // Skip 0 — InputPtrHandle reserves 0 as the null sentinel.
        uint _nextHandleId = 1;

        bool _isDisposed;

        internal InputNativeUniqueHeap(TrecsLog log)
        {
            _log = log;
            _allocations = new NativeHashMap<InputPtrHandle, InputAllocation>(
                1,
                Allocator.Persistent
            );
            _resolver = new InputNativeUniqueResolver(_allocations);
        }

        /// <summary>
        /// Burst-friendly resolver for <see cref="InputNativeUniquePtr{T}"/> reads
        /// inside jobs. Copy by value into job fields; stays valid for the job's
        /// duration.
        /// </summary>
        public ref InputNativeUniqueResolver Resolver
        {
            get
            {
                TrecsDebugAssert.That(!_isDisposed);
                return ref _resolver;
            }
        }

        public int NumLiveFrames
        {
            get
            {
                TrecsDebugAssert.That(!_isDisposed);
                return _handlesByFrame.Count;
            }
        }

        public int NumLiveAllocations
        {
            get
            {
                TrecsDebugAssert.That(!_isDisposed);
                return _allocations.Count;
            }
        }

        internal unsafe InputNativeUniquePtr<T> Alloc<T>(int frame, in T value)
            where T : unmanaged
        {
            // Main-thread gate lives on WorldAccessor.AssertCanAddInputsHeap,
            // which every public Alloc path (InputNativeUniquePtr.Alloc and
            // friends) calls before reaching here. Lifecycle methods below
            // (ClearAt*, ClearAll, Dispose, Serialize, Deserialize) reach the
            // heap via EntityInputQueue / EcsHeapAllocator instead, so they
            // keep their own in-heap AssertMainThread as defense-in-depth.
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(frame >= 0);

            var size = UnsafeUtility.SizeOf<T>();
            var alignment = UnsafeUtility.AlignOf<T>();
            var ptr = UnsafeUtility.Malloc(size, alignment, Allocator.Persistent);
            UnsafeUtility.WriteArrayElement(ptr, 0, value);

            var handle = new InputPtrHandle(_nextHandleId++);
            _allocations.Add(handle, new InputAllocation((IntPtr)ptr, size, TypeId<T>.Value));
            TrackHandle(frame, handle);
            _log.Trace(
                "Allocated input native unique type={0} handle={1} size={2} frame={3}",
                typeof(T),
                handle.Value,
                size,
                frame
            );
            return new InputNativeUniquePtr<T>(handle);
        }

        internal InputNativeUniquePtr<T> Alloc<T>(int frame)
            where T : unmanaged => Alloc<T>(frame, default);

        internal unsafe void* ResolveUnsafePtr<T>(InputPtrHandle handle)
            where T : unmanaged
        {
            AssertMainThread();
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(!handle.IsNull, "Cannot resolve null InputPtrHandle");
            if (!_allocations.TryGetValue(handle, out var alloc))
            {
                throw TrecsDebugAssert.CreateException(
                    "Attempted to resolve invalid InputNativeUniquePtr handle {0}",
                    handle.Value
                );
            }
            TrecsAssert.That(
                alloc.TypeHash == TypeId<T>.Value,
                "InputNativeUniquePtr.Read: handle {0} type mismatch (allocated as TypeId={1}, read as {2} with TypeId={3})",
                handle.Value,
                alloc.TypeHash.Value,
                typeof(T),
                TypeId<T>.Value.Value
            );
            return (void*)alloc.Ptr;
        }

        void TrackHandle(int frame, InputPtrHandle handle)
        {
            if (!_handlesByFrame.TryGetValue(frame, out var list))
            {
                list = _listPool.Count > 0 ? _listPool.Pop() : new List<InputPtrHandle>();
                TrecsDebugAssert.That(list.Count == 0);
                _handlesByFrame.Add(frame, list);
            }
            list.Add(handle);
        }

        internal void ClearAtOrAfterFrame(int frame)
        {
            AssertMainThread();
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(_frameRemoveBuffer.IsEmpty());

            foreach (var (f, _) in _handlesByFrame)
            {
                if (f >= frame)
                {
                    _frameRemoveBuffer.Add(f);
                }
            }
            foreach (var f in _frameRemoveBuffer)
            {
                ReleaseFrame(f);
            }
            _frameRemoveBuffer.Clear();
        }

        internal void ClearAtOrBeforeFrame(int frame)
        {
            AssertMainThread();
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(_frameRemoveBuffer.IsEmpty());

            foreach (var (f, _) in _handlesByFrame)
            {
                if (f <= frame)
                {
                    _frameRemoveBuffer.Add(f);
                }
            }
            foreach (var f in _frameRemoveBuffer)
            {
                ReleaseFrame(f);
            }
            _frameRemoveBuffer.Clear();
        }

        internal void ClearAll()
        {
            AssertMainThread();
            TrecsDebugAssert.That(!_isDisposed);
            foreach (var (_, list) in _handlesByFrame)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    FreeAllocation(list[i]);
                }
                list.Clear();
                _listPool.Push(list);
            }
            _handlesByFrame.Clear();
            TrecsDebugAssert.IsEqual(
                _allocations.Count,
                0,
                "InputNativeUniqueHeap allocation leak after ClearAll"
            );
        }

        void ReleaseFrame(int frame)
        {
            var list = _handlesByFrame[frame];
            for (int i = 0; i < list.Count; i++)
            {
                FreeAllocation(list[i]);
            }
            list.Clear();
            _listPool.Push(list);
            _handlesByFrame.RemoveMustExist(frame);
        }

        unsafe void FreeAllocation(InputPtrHandle handle)
        {
            var alloc = _allocations[handle];
            UnsafeUtility.Free((void*)alloc.Ptr, Allocator.Persistent);
            _allocations.Remove(handle);
        }

        internal void Dispose()
        {
            AssertMainThread();
            TrecsDebugAssert.That(!_isDisposed);
            ClearAll();
            _allocations.Dispose();
            _isDisposed = true;
        }

        /// <summary>
        /// Writes (frame, [handle, size, typeHash, bytes]) tuples. Handles are
        /// content-addressed monotonic IDs, so they round-trip directly; on
        /// Deserialize the heap re-allocates each buffer, registers it under
        /// the same handle + TypeId, and restores the handle counter so
        /// subsequent Allocs don't collide. Callers ClearAll before Deserialize,
        /// so direct assignment of the counter is safe — no merging with a live
        /// counter required.
        /// </summary>
        internal void Serialize(ISerializationWriter writer)
        {
            AssertMainThread();
            TrecsDebugAssert.That(!_isDisposed);

            writer.Write<uint>("IdCounter", _nextHandleId);
            writer.Write<int>("NumFrames", _handlesByFrame.Count);
            foreach (var (frame, list) in _handlesByFrame)
            {
                writer.Write<int>("Frame", frame);
                writer.Write<int>("NumHandles", list.Count);
                for (int i = 0; i < list.Count; i++)
                {
                    var handle = list[i];
                    var alloc = _allocations[handle];
                    writer.Write<uint>("Handle", handle.Value);
                    writer.Write<int>("Size", alloc.Size);
                    writer.Write<int>("TypeHash", alloc.TypeHash.Value);
                    EnsureScratchByteCapacity(alloc.Size);
                    if (alloc.Size > 0)
                    {
                        unsafe
                        {
                            fixed (byte* dst = _scratchBytes)
                            {
                                UnsafeUtility.MemCpy(dst, (void*)alloc.Ptr, alloc.Size);
                            }
                        }
                    }
                    writer.WriteBytes("Bytes", _scratchBytes, 0, alloc.Size);
                }
            }
        }

        internal void Deserialize(ISerializationReader reader)
        {
            AssertMainThread();
            TrecsDebugAssert.That(!_isDisposed);
            // Defensive: the contract is that callers ClearAll() before
            // Deserialize, but ClearAll is cheap so we belt-and-brace here so
            // a missed call from a future caller doesn't silently leak entries.
            ClearAll();

            _nextHandleId = reader.Read<uint>("IdCounter");
            var numFrames = reader.Read<int>("NumFrames");
            for (int i = 0; i < numFrames; i++)
            {
                var frame = reader.Read<int>("Frame");
                var numHandles = reader.Read<int>("NumHandles");
                for (int j = 0; j < numHandles; j++)
                {
                    var handleValue = reader.Read<uint>("Handle");
                    var size = reader.Read<int>("Size");
                    var typeHash = new TypeId(reader.Read<int>("TypeHash"));
                    EnsureScratchByteCapacity(size);
                    var actual = reader.ReadBytes("Bytes", ref _scratchBytes);
                    TrecsDebugAssert.IsEqual(
                        actual,
                        size,
                        "InputNativeUniqueHeap.Deserialize: bytes length mismatch"
                    );

                    unsafe
                    {
                        // Re-allocate at native alignment (16). The original T's
                        // exact alignment isn't on the wire — 16-byte alignment
                        // is a safe upper bound for any unmanaged type Burst
                        // cares about.
                        var ptr = UnsafeUtility.Malloc(Math.Max(size, 1), 16, Allocator.Persistent);
                        if (size > 0)
                        {
                            fixed (byte* src = _scratchBytes)
                            {
                                UnsafeUtility.MemCpy(ptr, src, size);
                            }
                        }
                        var handle = new InputPtrHandle(handleValue);
                        _allocations.Add(handle, new InputAllocation((IntPtr)ptr, size, typeHash));
                        TrackHandle(frame, handle);
                    }
                }
            }
            _log.Debug(
                "Deserialized {0} frames ({1} allocations) into InputNativeUniqueHeap",
                _handlesByFrame.Count,
                _allocations.Count
            );
        }

        static void AssertMainThread()
        {
            TrecsDebugAssert.That(
                UnityThreadHelper.IsMainThread,
                "InputNativeUniqueHeap mutators and main-thread Resolve must run on the main thread; "
                    + "Burst jobs use InputNativeUniqueResolver"
            );
        }

        byte[] _scratchBytes = Array.Empty<byte>();

        void EnsureScratchByteCapacity(int required)
        {
            if (_scratchBytes.Length < required)
            {
                var newCap = _scratchBytes.Length == 0 ? 1024 : _scratchBytes.Length * 2;
                while (newCap < required)
                {
                    newCap *= 2;
                }
                _scratchBytes = new byte[newCap];
            }
        }
    }
}
