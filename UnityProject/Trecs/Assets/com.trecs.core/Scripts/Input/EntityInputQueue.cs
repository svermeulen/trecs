using System;
using System.Collections.Generic;
using System.ComponentModel;
using Unity.Collections;
using Unity.Mathematics;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public class EntityInputQueue : IEntityInputQueue
    {
        static readonly TrecsLog _log = new(nameof(EntityInputQueue));

        readonly List<ResetToDefaultGroupInfo> _resetToDefaultGroups;
        readonly SystemRunner _systemRunner;
        readonly DisposeGroup _subscriptions = new();
        readonly List<IInputHistoryLocker> _historyLockers = new();
        readonly FrameScopedSharedHeap _frameScopedSharedHeap;
        readonly FrameScopedUniqueHeap _frameScopedUniqueHeap;
        readonly FrameScopedNativeSharedHeap _nativeFrameScopedSharedHeap;
        readonly FrameScopedNativeUniqueHeap _frameScopedNativeUniqueHeap;
        readonly DenseDictionary<int, ComponentTypeInfo> _componentTypeHelpers = new();
        readonly List<int> _frameTempRemoveBuffer = new();
        readonly List<EntityHandle> _removeQueueBuffer = new();

#if DEBUG
        readonly List<GroupComponentTypePair> _warnOnMissingInfos;
        readonly HashSet<GroupComponentTypePair> _warnOnMissingTempBuffer = new();
#endif

        SimpleSubject _systemRegistryPostApplyInputsEvent;
        WorldAccessor _accessor;
        int _maxClearFrame = -1;

        public EntityInputQueue(
            FrameScopedSharedHeap frameScopedSharedHeap,
            FrameScopedNativeSharedHeap nativeFrameScopedSharedHeap,
            FrameScopedUniqueHeap frameScopedUniqueHeap,
            FrameScopedNativeUniqueHeap frameScopedNativeUniqueHeap,
            SystemRunner systemRunner,
            WorldInfo worldDef
        )
        {
            _frameScopedSharedHeap = frameScopedSharedHeap;
            _nativeFrameScopedSharedHeap = nativeFrameScopedSharedHeap;
            _frameScopedUniqueHeap = frameScopedUniqueHeap;
            _frameScopedNativeUniqueHeap = frameScopedNativeUniqueHeap;
            _resetToDefaultGroups = new();
            _systemRunner = systemRunner;

#if DEBUG
            _warnOnMissingInfos = new();
#endif

            foreach (var group in worldDef.AllGroups)
            {
                var template = worldDef.GetResolvedTemplateForGroup(group);

                foreach (var componentDec in template.ComponentDeclarations)
                {
                    if (componentDec.IsInput)
                    {
                        if (
                            componentDec.MissingInputFrameBehaviour.Value
                            == MissingInputFrameBehaviour.ResetToDefault
                        )
                        {
                            _resetToDefaultGroups.Add(
                                new ResetToDefaultGroupInfo
                                {
                                    GroupIndex = group,
                                    ComponentBuilder = componentDec.Builder,
                                }
                            );
                        }

#if DEBUG
                        if (componentDec.WarnOnMissingInput)
                        {
                            _warnOnMissingInfos.Add(
                                new GroupComponentTypePair
                                {
                                    GroupIndex = group,
                                    ComponentType = componentDec.ComponentType,
                                }
                            );
                        }
#endif
                    }
                }
            }

            systemRunner.ReadyToApplyInputs.Subscribe(OnReadyForInputs).AddTo(_subscriptions);
        }

        public WorldAccessor Accessor
        {
            set
            {
                Assert.IsNull(_accessor);
                Assert.IsNotNull(value);
                _accessor = value;
            }
        }

        internal void SetPostApplyInputsSubject(SimpleSubject systemRegistrySubject)
        {
            Assert.IsNull(_systemRegistryPostApplyInputsEvent);
            _systemRegistryPostApplyInputsEvent = systemRegistrySubject;
        }

        public bool HasInputFrame<T>(int frame, EntityHandle entityHandle)
            where T : unmanaged, IEntityComponent
        {
            var typeHash = TypeIdProvider.GetTypeId<T>();

            if (!_componentTypeHelpers.TryGetValue(typeHash, out var componentInfo))
            {
                return false;
            }

            var helper = (ComponentTypeHelper<T>)componentInfo.Helper;

            var key = new FrameEntityHandlePair(frame, entityHandle);
            return helper.Values.ContainsKey(key);
        }

        // NOTE - if you use this, you have to be really sure you don't add items
        // while still using the ref
        public ref T GetInputRefUnsafe<T>(int frame, EntityHandle entityHandle)
            where T : unmanaged, IEntityComponent
        {
            var typeHash = TypeIdProvider.GetTypeId<T>();

            if (_componentTypeHelpers.TryGetValue(typeHash, out var componentInfo))
            {
                var helper = (ComponentTypeHelper<T>)componentInfo.Helper;

                var key = new FrameEntityHandlePair(frame, entityHandle);
                if (helper.Values.ContainsKey(key))
                {
                    return ref helper.Values.GetValueByRef(key);
                }
            }

            throw Assert.CreateException(
                "Input not found for frame {} and entity {}",
                frame,
                entityHandle
            );
        }

        public bool TryGetInput<T>(int frame, EntityHandle entityHandle, out T value)
            where T : unmanaged, IEntityComponent
        {
            var typeHash = TypeIdProvider.GetTypeId<T>();

            if (_componentTypeHelpers.TryGetValue(typeHash, out var componentInfo))
            {
                var helper = (ComponentTypeHelper<T>)componentInfo.Helper;

                var key = new FrameEntityHandlePair(frame, entityHandle);
                if (helper.Values.ContainsKey(key))
                {
                    value = helper.Values.GetValueByRef(key);
                    return true;
                }
            }

            value = default;
            return false;
        }

        void SetOrAddInput<T>(int frame, EntityHandle entityHandle, in T value, bool existsOk)
            where T : unmanaged, IEntityComponent
        {
            var typeHash = TypeIdProvider.GetTypeId<T>();

            if (!_componentTypeHelpers.TryGetValue(typeHash, out var componentInfo))
            {
                componentInfo = new() { Helper = new ComponentTypeHelper<T>() };

                _componentTypeHelpers.Add(typeHash, componentInfo);
            }

            var key = new FrameEntityHandlePair(frame, entityHandle);
            var values = ((ComponentTypeHelper<T>)componentInfo.Helper).Values;

            if (!existsOk)
            {
                Assert.That(
                    !values.ContainsKey(key),
                    "Input already exists for frame {} and entity {}",
                    frame,
                    entityHandle
                );
            }

            values.GetOrAdd(key) = value;

            if (!componentInfo.FrameEntries.TryGetValue(frame, out var entityHandles))
            {
                entityHandles = SpawnEntityHandleValueIdSet(componentInfo);
                componentInfo.FrameEntries.Add(frame, entityHandles);
            }

            entityHandles.Add(entityHandle);
        }

        public void AddInput<T>(int frame, EntityHandle entityHandle, in T value)
            where T : unmanaged, IEntityComponent
        {
            SetOrAddInput<T>(frame, entityHandle, value, existsOk: false);
        }

        public void SetInput<T>(int frame, EntityHandle entityHandle, in T value)
            where T : unmanaged, IEntityComponent
        {
            SetOrAddInput<T>(frame, entityHandle, value, existsOk: true);
        }

        public void ClearFutureInputsAfterOrAt(int frame)
        {
            foreach (var (_, info) in _componentTypeHelpers)
            {
                _frameTempRemoveBuffer.Clear();

                foreach (var (candidateFrame, _) in info.FrameEntries)
                {
                    if (candidateFrame >= frame)
                    {
                        _frameTempRemoveBuffer.Add(candidateFrame);
                    }
                }

                foreach (var key in _frameTempRemoveBuffer)
                {
                    var wasRemoved = info.FrameEntries.TryRemove(key, out var entityHandleSet);
                    Assert.That(wasRemoved);

                    foreach (var entityHandle in entityHandleSet)
                    {
                        info.Helper.Remove(new(key, entityHandle));
                    }
                    entityHandleSet.Clear();

                    DespawnEntityHandleSet(info, entityHandleSet);
                }
            }

            _frameScopedUniqueHeap.ClearAtOrAfterFrame(frame);
            _frameScopedSharedHeap.ClearAtOrAfterFrame(frame);
            _nativeFrameScopedSharedHeap.ClearAtOrAfterFrame(frame);
            _frameScopedNativeUniqueHeap.ClearAtOrAfterFrame(frame);
        }

        public void ClearAllInputs()
        {
            _log.Trace("Clearing all inputs");

            foreach (var (_, info) in _componentTypeHelpers)
            {
                foreach (var (frame, entityHandleSet) in info.FrameEntries)
                {
                    foreach (var entityHandle in entityHandleSet)
                    {
                        info.Helper.Remove(new(frame, entityHandle));
                    }
                    entityHandleSet.Clear();

                    DespawnEntityHandleSet(info, entityHandleSet);
                }

                info.FrameEntries.Clear();
            }

            _frameScopedUniqueHeap.ClearAll();
            _frameScopedSharedHeap.ClearAll();
            _nativeFrameScopedSharedHeap.ClearAll();
            _frameScopedNativeUniqueHeap.ClearAll();
        }

        DenseHashSet<EntityHandle> SpawnEntityHandleValueIdSet(ComponentTypeInfo info)
        {
            if (info.EntityHandleSetPool.Count > 0)
            {
                var result = info.EntityHandleSetPool.Pop();
                Assert.That(result.IsEmpty);
                return result;
            }

            using (TrecsProfiling.Start("Allocating new DenseHashSet<EntityHandle>"))
            {
                return new DenseHashSet<EntityHandle>(8);
            }
        }

        void DespawnEntityHandleSet(
            ComponentTypeInfo info,
            DenseHashSet<EntityHandle> entityHandleSet
        )
        {
            Assert.That(entityHandleSet.IsEmpty);
            info.EntityHandleSetPool.Push(entityHandleSet);
        }

        public void ClearInputsBeforeOrAt(int frame)
        {
            _log.Trace("ClearInputsBeforeOrAt frame {}", frame);

            foreach (var (_, info) in _componentTypeHelpers)
            {
                _frameTempRemoveBuffer.Clear();

                foreach (var (candidateFrame, _) in info.FrameEntries)
                {
                    if (candidateFrame <= frame)
                    {
                        _frameTempRemoveBuffer.Add(candidateFrame);
                    }
                }

                foreach (var key in _frameTempRemoveBuffer)
                {
                    var wasRemoved = info.FrameEntries.TryRemove(key, out var entityHandleSet);
                    Assert.That(wasRemoved);

                    foreach (var entityHandle in entityHandleSet)
                    {
                        info.Helper.Remove(new(key, entityHandle));
                    }
                    entityHandleSet.Clear();

                    DespawnEntityHandleSet(info, entityHandleSet);
                }
            }

            _frameScopedUniqueHeap.ClearAtOrBeforeFrame(frame);
            _frameScopedSharedHeap.ClearAtOrBeforeFrame(frame);
            _nativeFrameScopedSharedHeap.ClearAtOrBeforeFrame(frame);
            _frameScopedNativeUniqueHeap.ClearAtOrBeforeFrame(frame);
        }

        // Note: these Serialize/Deserialize methods are used by the recording system
        // for deterministic replay, not by snapshot serialization. Snapshots don't need
        // input queue state because the component values themselves (which include the
        // last-applied input for RetainCurrent components) are already captured in the
        // WorldStateSerializer snapshot.
        public void Serialize(ITrecsSerializationWriter writer)
        {
            writer.Write("NumHelpers", _componentTypeHelpers.Count);
            long bytesStart;

            foreach (var (typeHash, info) in _componentTypeHelpers)
            {
                bytesStart = writer.NumBytesWritten;
                writer.Write("ComponentType", info.Helper.ComponentType);
                Assert.IsNotNull(info.Helper);
                info.Helper.SerializeValues(writer);

                writer.Write("NumFrameEntries", info.FrameEntries.Count);

                foreach (var (frame, entityHandleSet) in info.FrameEntries)
                {
                    writer.Write("Frame", frame);
                    writer.Write<DenseHashSet<EntityHandle>>("Erefs", entityHandleSet);
                }

                _log.Debug(
                    "Serialized {0.00} kb for type {} ({} frames)",
                    (writer.NumBytesWritten - bytesStart) / 1024f,
                    info.Helper.ComponentType,
                    info.FrameEntries.Count
                );
            }

            bytesStart = writer.NumBytesWritten;
            _frameScopedUniqueHeap.Serialize(writer);
            _log.Debug(
                "Serialized {0.00} kb for FrameScopedUniqueHeap",
                (writer.NumBytesWritten - bytesStart) / 1024f
            );

            bytesStart = writer.NumBytesWritten;
            _frameScopedSharedHeap.Serialize(writer);
            _log.Debug(
                "Serialized {0.00} kb for FrameScopedSharedHeap",
                (writer.NumBytesWritten - bytesStart) / 1024f
            );

            bytesStart = writer.NumBytesWritten;
            _nativeFrameScopedSharedHeap.Serialize(writer);
            _log.Debug(
                "Serialized {0.00} kb for FrameScopedNativeSharedHeap",
                (writer.NumBytesWritten - bytesStart) / 1024f
            );

            bytesStart = writer.NumBytesWritten;
            _frameScopedNativeUniqueHeap.Serialize(writer);
            _log.Debug(
                "Serialized {0.00} kb for FrameScopedNativeUniqueHeap",
                (writer.NumBytesWritten - bytesStart) / 1024f
            );
        }

        public void Deserialize(ITrecsSerializationReader reader)
        {
            _log.Trace("Deserializing EntityInputQueue");

            foreach (var (_, info) in _componentTypeHelpers)
            {
                foreach (var entityHandleSet in info.FrameEntries)
                {
                    info.EntityHandleSetPool.Push(entityHandleSet.Value);
                }
                info.FrameEntries.Clear();
                info.Helper.Clear();
            }

            var numHelpers = reader.Read<int>("NumHelpers");

            for (int i = 0; i < numHelpers; i++)
            {
                var componentType = reader.Read<Type>("ComponentType");
                var typeHash = TypeIdProvider.GetTypeId(componentType);

                if (!_componentTypeHelpers.TryGetValue(typeHash, out var info))
                {
                    info = new ComponentTypeInfo();
                    _componentTypeHelpers.Add(typeHash, info);
                }

                if (info.Helper == null)
                {
                    info.Helper = CreateHelperForType(componentType);
                }
                info.Helper.DeserializeValues(reader);
                Assert.IsNotNull(info.Helper);

                var numFrameEntries = reader.Read<int>("NumFrameEntries");

                for (int k = 0; k < numFrameEntries; k++)
                {
                    var frame = reader.Read<int>("Frame");
                    var entityHandleSet = SpawnEntityHandleValueIdSet(info);
                    reader.ReadInPlace<DenseHashSet<EntityHandle>>("Erefs", entityHandleSet);

                    info.FrameEntries.Add(frame, entityHandleSet);
                }
            }

            _frameScopedUniqueHeap.Deserialize(reader);
            _frameScopedSharedHeap.Deserialize(reader);
            _nativeFrameScopedSharedHeap.Deserialize(reader);
            _frameScopedNativeUniqueHeap.Deserialize(reader);
        }

        /// <summary>
        /// Remaps all stored input frame numbers by adding the given offset.
        /// Used for InputsOnly playback mode where inputs need to be applied
        /// relative to the current frame rather than the original recording frame.
        /// </summary>
        public void RemapFrameOffsets(int frameOffset)
        {
            if (frameOffset == 0)
            {
                return;
            }

            _log.Debug("Remapping input frame offsets by {}", frameOffset);

            foreach (var (_, info) in _componentTypeHelpers)
            {
                // Collect all frame entries to remap
                var framesToRemap =
                    new List<(int oldFrame, DenseHashSet<EntityHandle> entityHandles)>();
                foreach (var (frame, entityHandleSet) in info.FrameEntries)
                {
                    framesToRemap.Add((frame, entityHandleSet));
                }

                // Clear and rebuild with new frame numbers
                info.FrameEntries.Clear();
                foreach (var (oldFrame, entityHandleSet) in framesToRemap)
                {
                    var newFrame = oldFrame + frameOffset;
                    info.FrameEntries.Add(newFrame, entityHandleSet);
                }

                // Remap the helper's internal storage
                info.Helper.RemapFrameOffsets(frameOffset);
            }

            _frameScopedUniqueHeap.RemapFrameOffsets(frameOffset);
            _frameScopedSharedHeap.RemapFrameOffsets(frameOffset);
            _nativeFrameScopedSharedHeap.RemapFrameOffsets(frameOffset);
            _frameScopedNativeUniqueHeap.RemapFrameOffsets(frameOffset);
        }

        internal void ResetInputs<T>(GroupIndex group)
            where T : unmanaged, IEntityComponent
        {
            var values = _accessor.ComponentBuffer<T>(group).Write;
            var count = _accessor.CountEntitiesInGroup(group);

            for (int i = 0; i < count; i++)
            {
                values[i] = default;
            }
        }

        internal void ApplyInputs<T>(
            DenseHashSet<EntityHandle> entityHandles,
            int frame,
            NativeDenseDictionary<FrameEntityHandlePair, T> values
        )
            where T : unmanaged, IEntityComponent
        {
            // Note here that we remove input entries regardless of the value for max clear frame
            // This is because when they are removed here, this should have no effect
            // They are either invalid or redundant
            _removeQueueBuffer.Clear();

            var world = _accessor;

            foreach (var entityHandle in entityHandles)
            {
                if (!entityHandle.Exists(world))
                {
                    _log.Warning(
                        "Attempted to apply input for non-existing entity {}",
                        entityHandle
                    );
                    continue;
                }

                var valuesKey = new FrameEntityHandlePair(frame, entityHandle);
                ref readonly var desiredValue = ref values.GetValueByRef(valuesKey);

                var entityIndex = entityHandle.ToIndex(world);

                if (!world.TryComponent<T>(entityIndex, out var component))
                {
                    _removeQueueBuffer.Add(entityHandle);
                    values.Remove(valuesKey);

                    _log.Warning(
                        "Found input with type {} for entity but entity doesn't exist. Discarding input",
                        typeof(T)
                    );
                    continue;
                }

#if DEBUG
                _warnOnMissingTempBuffer.Remove(
                    new GroupComponentTypePair()
                    {
                        GroupIndex = entityIndex.GroupIndex,
                        ComponentType = typeof(T),
                    }
                );
#endif

                ref var value = ref component.Write;

                if (UnmanagedUtil.BlittableEquals(value, desiredValue))
                {
                    // This means the input is redundant, so there is no need to store it
                    _removeQueueBuffer.Add(entityHandle);
                    values.Remove(valuesKey);
                }
                else
                {
                    value = desiredValue;
                }
            }

            foreach (var entityHandle in _removeQueueBuffer)
            {
                entityHandles.RemoveMustExist(entityHandle);
            }

            _removeQueueBuffer.Clear();
        }

        public void AddHistoryLocker(IInputHistoryLocker locker)
        {
            Assert.That(!_historyLockers.Contains(locker));
            _historyLockers.Add(locker);
        }

        public void RemoveHistoryLocker(IInputHistoryLocker locker)
        {
            var wasRemoved = _historyLockers.Remove(locker);
            Assert.That(wasRemoved);
        }

        public int GetMaxClearFrame()
        {
            return CalculateMaxClearFrame();
        }

        int CalculateMaxClearFrame()
        {
            // We need to do -1 for the heap allocated inputs
            // since we don't want to dispose those until next frame, since they could be used this frame
            int maxClearFrame = _systemRunner.FixedFrame - 1;

            foreach (var locker in _historyLockers)
            {
                var lockerMaxClearFrame = locker.MaxClearFrame;

                if (lockerMaxClearFrame.HasValue)
                {
                    maxClearFrame = math.min(maxClearFrame, lockerMaxClearFrame.Value);
                }
            }

            return maxClearFrame;
        }

        void OnReadyForInputs()
        {
            using (TrecsProfiling.Start("Resetting Input Values"))
            {
                foreach (var info in _resetToDefaultGroups)
                {
                    info.ComponentBuilder.ResetInputs(this, info.GroupIndex);
                }
            }

#if DEBUG
            _warnOnMissingTempBuffer.Clear();

            foreach (var info in _warnOnMissingInfos)
            {
                _warnOnMissingTempBuffer.Add(info);
            }
#endif

            var frame = _systemRunner.FixedFrame;

#if TRECS_IS_PROFILING
            using (TrecsProfiling.Start("Applying Inputs"))
#endif
            {
                foreach (var (_, info) in _componentTypeHelpers)
                {
                    if (info.FrameEntries.TryGetValue(frame, out var entityHandles))
                    {
                        info.Helper.ApplyInputs(this, entityHandles, frame);

                        if (entityHandles.IsEmpty)
                        {
                            info.FrameEntries.RemoveMustExist(frame);
                            DespawnEntityHandleSet(info, entityHandles);
                        }
                    }
                }
            }

#if DEBUG
            foreach (var info in _warnOnMissingTempBuffer)
            {
                if (_accessor.CountEntitiesInGroup(info.GroupIndex) > 0)
                {
                    _log.Warning(
                        "No frame data found for input component {} on frame {}",
                        info.ComponentType,
                        frame
                    );
                }
            }
            _warnOnMissingTempBuffer.Clear();
#endif

#if TRECS_IS_PROFILING
            using (TrecsProfiling.Start("Clearing old input"))
#endif
            {
                _maxClearFrame = CalculateMaxClearFrame();
                // _dbg.Text("MaxClearFrame: {}", _maxClearFrame);

                ClearInputsBeforeOrAt(_maxClearFrame);
            }

            _systemRegistryPostApplyInputsEvent?.Invoke();
        }

        public void Dispose()
        {
            _subscriptions.Dispose();

            foreach (var (_, info) in _componentTypeHelpers)
            {
                info.Helper.Dispose();
            }
            _componentTypeHelpers.Clear();

            // Note: post-apply-inputs event is owned by EventsManager, not EntityInputQueue
        }

        internal readonly struct FrameEntityHandlePair : IEquatable<FrameEntityHandlePair>
        {
            public readonly int Frame;
            public readonly EntityHandle EntityHandle;

            public FrameEntityHandlePair(int frame, EntityHandle entityHandle)
            {
                Frame = frame;
                EntityHandle = entityHandle;
            }

            public override readonly bool Equals(object obj)
            {
                return obj is FrameEntityHandlePair other && Equals(other);
            }

            public readonly bool Equals(FrameEntityHandlePair other)
            {
                return Frame == other.Frame && EntityHandle.Equals(other.EntityHandle);
            }

            public override readonly int GetHashCode()
            {
                // we don't want to use HashCode.Combine because
                // it's not deterministic across restarts
                return unchecked((int)math.hash(new int2(Frame, EntityHandle.GetStableHashCode())));
            }

            public static bool operator ==(FrameEntityHandlePair left, FrameEntityHandlePair right)
            {
                return left.Equals(right);
            }

            public static bool operator !=(FrameEntityHandlePair left, FrameEntityHandlePair right)
            {
                return !(left == right);
            }
        }

        class ResetToDefaultGroupInfo
        {
            public GroupIndex GroupIndex;
            public IComponentBuilder ComponentBuilder;
        }

        struct GroupComponentTypePair : IEquatable<GroupComponentTypePair>
        {
            public GroupIndex GroupIndex;
            public Type ComponentType;

            public readonly bool Equals(GroupComponentTypePair other)
            {
                return GroupIndex == other.GroupIndex && ComponentType == other.ComponentType;
            }

            public override readonly bool Equals(object obj)
            {
                return obj is GroupComponentTypePair other && Equals(other);
            }

            public override readonly int GetHashCode()
            {
                return unchecked(
                    (int)math.hash(new int2(GroupIndex.GetHashCode(), ComponentType.GetHashCode()))
                );
            }

            public static bool operator ==(
                GroupComponentTypePair left,
                GroupComponentTypePair right
            )
            {
                return left.Equals(right);
            }

            public static bool operator !=(
                GroupComponentTypePair left,
                GroupComponentTypePair right
            )
            {
                return !(left == right);
            }
        }

        internal interface IComponentTypeHelper : IDisposable
        {
            void Remove(FrameEntityHandlePair key);

            bool ContainsKey(FrameEntityHandlePair key);

            Type ComponentType { get; }

            void Clear();

            void ApplyInputs(
                EntityInputQueue entityInputQueue,
                DenseHashSet<EntityHandle> entityHandles,
                int frame
            );

            void RemapFrameOffsets(int frameOffset);

            void SerializeValues(ITrecsSerializationWriter writer);
            void DeserializeValues(ITrecsSerializationReader reader);
        }

        internal class ComponentTypeHelper<T> : IComponentTypeHelper
            where T : unmanaged, IEntityComponent
        {
            public NativeDenseDictionary<FrameEntityHandlePair, T> Values = new(
                10,
                Allocator.Persistent
            );

            public Type ComponentType
            {
                get { return typeof(T); }
            }

            public void Dispose()
            {
                Values.Dispose();
            }

            public bool ContainsKey(FrameEntityHandlePair key)
            {
                return Values.ContainsKey(key);
            }

            public void Remove(FrameEntityHandlePair key)
            {
                var wasRemoved = Values.Remove(key);
                Assert.That(wasRemoved);
            }

            public void ApplyInputs(
                EntityInputQueue entityInputQueue,
                DenseHashSet<EntityHandle> entityHandles,
                int frame
            )
            {
                entityInputQueue.ApplyInputs<T>(entityHandles, frame, Values);
            }

            public void Clear()
            {
                Values.Clear();
            }

            public void RemapFrameOffsets(int frameOffset)
            {
                // We need to rebuild the dictionary with remapped frame keys
                var oldValues = new List<(FrameEntityHandlePair key, T value)>();
                foreach (var (key, value) in Values)
                {
                    oldValues.Add((key, value));
                }

                Values.Clear();

                foreach (var (oldKey, value) in oldValues)
                {
                    var newKey = new FrameEntityHandlePair(
                        oldKey.Frame + frameOffset,
                        oldKey.EntityHandle
                    );
                    Values.Add(newKey, value);
                }
            }

            public void SerializeValues(ITrecsSerializationWriter writer)
            {
                Values.SerializeValues(writer);
            }

            public void DeserializeValues(ITrecsSerializationReader reader)
            {
                Values.DeserializeValues(reader);
            }
        }

        static IComponentTypeHelper CreateHelperForType(Type componentType)
        {
            var helperType = typeof(ComponentTypeHelper<>).MakeGenericType(componentType);
            return (IComponentTypeHelper)Activator.CreateInstance(helperType);
        }

        class ComponentTypeInfo
        {
            public IComponentTypeHelper Helper;
            public readonly DenseDictionary<int, DenseHashSet<EntityHandle>> FrameEntries = new();
            public readonly Stack<DenseHashSet<EntityHandle>> EntityHandleSetPool = new();
        }
    }
}
