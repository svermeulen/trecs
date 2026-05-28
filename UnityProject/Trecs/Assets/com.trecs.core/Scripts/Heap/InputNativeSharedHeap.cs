using System;
using System.Collections.Generic;
using Trecs.Collections;
using Trecs.Internal;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Heap that tracks refcount handles for input-allocated unmanaged shared
    /// blobs (<see cref="InputNativeSharedPtr{T}"/>). The blob bytes themselves
    /// live in the shared <see cref="BlobCache"/>; this heap owns one
    /// <see cref="PtrHandle"/> per allocation that keeps the blob's refcount
    /// pinned for the lifetime of the input frame. When a frame is trimmed,
    /// the handles for that frame are released in bulk and the cache evicts
    /// the blob if its refcount drops to zero.
    ///
    /// <para>Provides its own <see cref="InputNativeSharedPtrResolver"/> independent
    /// from <see cref="NativeSharedHeap"/>. Input allocations happen during the input
    /// phase (before fixed update); jobs only read during fixed update, so there is
    /// no concurrent read+write and no pending queue is needed.</para>
    /// </summary>
    public sealed class InputNativeSharedHeap
    {
        readonly TrecsLog _log;
        readonly BlobCache _store;

        readonly IterableDictionary<int, List<Entry>> _entriesByFrame = new();
        readonly Stack<List<Entry>> _listPool = new();
        readonly List<int> _frameRemoveBuffer = new();

        NativeHashMap<BlobId, InputNativeSharedHeapEntry> _resolverEntries;
        readonly Dictionary<BlobId, int> _blobIdRefCount = new();
        InputNativeSharedPtrResolver _resolver;

        bool _isDisposed;

        readonly struct Entry
        {
            public readonly BlobId BlobId;
            public readonly PtrHandle CacheHandle;

            public Entry(BlobId blobId, PtrHandle cacheHandle)
            {
                BlobId = blobId;
                CacheHandle = cacheHandle;
            }
        }

        public InputNativeSharedHeap(TrecsLog log, BlobCache store)
        {
            _log = log;
            _store = store;
            _resolverEntries = new NativeHashMap<BlobId, InputNativeSharedHeapEntry>(
                16,
                Allocator.Persistent
            );
            _resolver = new InputNativeSharedPtrResolver(_resolverEntries);
        }

        public int NumLiveFrames
        {
            get
            {
                TrecsDebugAssert.That(!_isDisposed);
                return _entriesByFrame.Count;
            }
        }

        public ref InputNativeSharedPtrResolver Resolver
        {
            get
            {
                TrecsDebugAssert.That(!_isDisposed);
                return ref _resolver;
            }
        }

        internal InputNativeSharedHeapEntry ResolveEntry<T>(BlobId blobId)
            where T : unmanaged
        {
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(!blobId.IsNull, "Attempted to resolve null blob address");

            TrecsAssert.That(
                _resolverEntries.TryGetValue(blobId, out var entry),
                "InputNativeSharedHeap could not resolve blob {0}",
                blobId.Value
            );

            TrecsAssert.That(
                entry.TypeHash == TypeId<T>.Value.Value,
                "Type hash mismatch for blob {0}: stored {1} != requested {2}",
                blobId.Value,
                entry.TypeHash,
                TypeId<T>.Value.Value
            );

            return entry;
        }

        internal InputNativeSharedPtr<T> Alloc<T>(int frame, BlobId blobId, in T value)
            where T : unmanaged
        {
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(frame >= 0);
            var handle = _store.AllocNativeBlob<T>(blobId, in value);
            TrackEntry(frame, handle.BlobId, handle.Handle);
            AddToResolver<T>(handle.BlobId);
            _log.Trace(
                "Allocated input native shared type={0} blobId={1} frame={2}",
                typeof(T),
                handle.BlobId,
                frame
            );
            return new InputNativeSharedPtr<T>(handle.BlobId);
        }

        internal bool TryAcquire<T>(int frame, BlobId blobId, out InputNativeSharedPtr<T> ptr)
            where T : unmanaged
        {
            TrecsDebugAssert.That(!_isDisposed);
            if (!_store.ContainsNativeBlob<T>(blobId, updateAccessTime: true))
            {
                ptr = default;
                return false;
            }
            var handle = _store.CreateHandle(blobId);
            TrackEntry(frame, blobId, handle);
            AddToResolver<T>(blobId);
            ptr = new InputNativeSharedPtr<T>(blobId);
            return true;
        }

        internal InputNativeSharedPtr<T> Acquire<T>(int frame, BlobId blobId)
            where T : unmanaged
        {
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(
                _store.ContainsNativeBlob<T>(blobId, updateAccessTime: false),
                "Acquire: no native blob exists at BlobId {0}",
                blobId
            );
            var handle = _store.CreateHandle(blobId);
            TrackEntry(frame, blobId, handle);
            AddToResolver<T>(blobId);
            return new InputNativeSharedPtr<T>(blobId);
        }

        void AddToResolver<T>(BlobId blobId)
            where T : unmanaged
        {
            if (_blobIdRefCount.TryGetValue(blobId, out var count))
            {
                _blobIdRefCount[blobId] = count + 1;
                return;
            }

            _blobIdRefCount[blobId] = 1;

            var ptr = _store.GetNativeBlobPtr(blobId, TypeId<T>.Value.Value);
            var burstTypeHash = TypeId<T>.Value.Value;

            EnsureResolverCapacity(_resolverEntries.Count + 1);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            var safety = AtomicSafetyHandle.Create();
            AtomicSafetyHandle.SetBumpSecondaryVersionOnScheduleWrite(safety, true);
            _resolverEntries.Add(
                blobId,
                new InputNativeSharedHeapEntry(burstTypeHash, ptr, safety)
            );
#else
            _resolverEntries.Add(blobId, new InputNativeSharedHeapEntry(burstTypeHash, ptr));
#endif
        }

        void RemoveFromResolver(BlobId blobId)
        {
            if (!_blobIdRefCount.TryGetValue(blobId, out var count))
                return;

            count -= 1;
            if (count > 0)
            {
                _blobIdRefCount[blobId] = count;
                return;
            }

            _blobIdRefCount.Remove(blobId);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (_resolverEntries.TryGetValue(blobId, out var entry))
            {
                AtomicSafetyHandle.CheckDeallocateAndThrow(entry.Safety);
                AtomicSafetyHandle.Release(entry.Safety);
            }
#endif
            _resolverEntries.Remove(blobId);
        }

        void EnsureResolverCapacity(int needed)
        {
            if (_resolverEntries.Capacity >= needed)
                return;
            var newCapacity = Math.Max(needed, _resolverEntries.Capacity * 2);
            _resolverEntries.Capacity = newCapacity;
        }

        void TrackEntry(int frame, BlobId blobId, PtrHandle cacheHandle)
        {
            if (!_entriesByFrame.TryGetValue(frame, out var list))
            {
                list = _listPool.Count > 0 ? _listPool.Pop() : new List<Entry>();
                TrecsDebugAssert.That(list.Count == 0);
                _entriesByFrame.Add(frame, list);
            }
            list.Add(new Entry(blobId, cacheHandle));
        }

        internal void ClearAtOrAfterFrame(int frame)
        {
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(_frameRemoveBuffer.IsEmpty());

            foreach (var (f, _) in _entriesByFrame)
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
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(_frameRemoveBuffer.IsEmpty());

            foreach (var (f, _) in _entriesByFrame)
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
            TrecsDebugAssert.That(!_isDisposed);
            foreach (var (_, list) in _entriesByFrame)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    _store.DisposeHandle(list[i].CacheHandle);
                }
                list.Clear();
                _listPool.Push(list);
            }
            _entriesByFrame.Clear();

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            foreach (var kvp in _resolverEntries)
            {
                AtomicSafetyHandle.CheckDeallocateAndThrow(kvp.Value.Safety);
                AtomicSafetyHandle.Release(kvp.Value.Safety);
            }
#endif
            _resolverEntries.Clear();
            _blobIdRefCount.Clear();
        }

        void ReleaseFrame(int frame)
        {
            var list = _entriesByFrame.RemoveAndGet(frame);
            for (int i = 0; i < list.Count; i++)
            {
                _store.DisposeHandle(list[i].CacheHandle);
                RemoveFromResolver(list[i].BlobId);
            }
            list.Clear();
            _listPool.Push(list);
        }

        internal void Serialize(ISerializationWriter writer)
        {
            TrecsDebugAssert.That(!_isDisposed);

            writer.Write<int>("NumFrames", _entriesByFrame.Count);
            foreach (var (frame, list) in _entriesByFrame)
            {
                writer.Write<int>("Frame", frame);
                writer.Write<int>("NumEntries", list.Count);
                for (int i = 0; i < list.Count; i++)
                {
                    writer.Write<BlobId>("BlobId", list[i].BlobId);
                }
            }
        }

        internal void Deserialize(ISerializationReader reader)
        {
            TrecsDebugAssert.That(!_isDisposed);
            ClearAll();

            var numFrames = reader.Read<int>("NumFrames");
            for (int i = 0; i < numFrames; i++)
            {
                var frame = reader.Read<int>("Frame");
                var numEntries = reader.Read<int>("NumEntries");
                for (int k = 0; k < numEntries; k++)
                {
                    var blobId = reader.Read<BlobId>("BlobId");
                    var handle = _store.CreateHandle(blobId);
                    TrackEntry(frame, blobId, handle);

                    // Rebuild resolver entries from BlobCache
                    if (!_blobIdRefCount.ContainsKey(blobId))
                    {
                        _blobIdRefCount[blobId] = 1;
                        var metadata = _store.GetBlobMetadata(blobId);
                        var ptr = _store.GetNativeBlobPtr(blobId, metadata.TypeId.Value);

                        EnsureResolverCapacity(_resolverEntries.Count + 1);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                        var safety = AtomicSafetyHandle.Create();
                        AtomicSafetyHandle.SetBumpSecondaryVersionOnScheduleWrite(safety, true);
                        _resolverEntries.Add(
                            blobId,
                            new InputNativeSharedHeapEntry(metadata.TypeId.Value, ptr, safety)
                        );
#else
                        _resolverEntries.Add(
                            blobId,
                            new InputNativeSharedHeapEntry(metadata.TypeId.Value, ptr)
                        );
#endif
                    }
                    else
                    {
                        _blobIdRefCount[blobId] += 1;
                    }
                }
            }
            _log.Debug("Deserialized {0} frames into InputNativeSharedHeap", _entriesByFrame.Count);
        }

        internal void Dispose()
        {
            TrecsDebugAssert.That(!_isDisposed);
            ClearAll();
            _resolverEntries.Dispose();
            _isDisposed = true;
        }
    }
}
