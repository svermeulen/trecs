using System;
using System.Collections.Generic;
using System.ComponentModel;
using Trecs.Collections;
using Unity.Collections;
using Unity.Mathematics;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class EntityInputQueue : IEntityInputQueue
    {
        readonly TrecsLog _log;

        readonly List<ResetGroupInfo> _resetGroups;
        readonly List<IInputHistoryLocker> _historyLockers = new();
        readonly InputSharedHeap _inputSharedHeap;
        readonly InputUniqueHeap _inputUniqueHeap;
        readonly InputNativeSharedHeap _inputNativeSharedHeap;
        readonly InputNativeUniqueHeap _inputNativeUniqueHeap;
        readonly IterableDictionary<TypeId, ComponentTypeInfo> _componentTypeHelpersMap = new();
        readonly List<int> _frameTempRemoveBuffer = new();
        readonly List<EntityHandle> _removeQueueBuffer = new();

        SimpleSubject _systemRegistryInputsAppliedEvent;
        WorldAccessor _accessor;
        int _maxClearFrame = -1;

        public EntityInputQueue(
            TrecsLog log,
            InputSharedHeap inputSharedHeap,
            InputNativeSharedHeap inputNativeSharedHeap,
            InputUniqueHeap inputUniqueHeap,
            InputNativeUniqueHeap inputNativeUniqueHeap,
            WorldInfo worldDef
        )
        {
            _log = log;
            _inputSharedHeap = inputSharedHeap;
            _inputNativeSharedHeap = inputNativeSharedHeap;
            _inputUniqueHeap = inputUniqueHeap;
            _inputNativeUniqueHeap = inputNativeUniqueHeap;
            _resetGroups = new();

            foreach (var group in worldDef.AllGroups)
            {
                var template = worldDef.GetResolvedTemplateForGroup(group);

                foreach (var componentDec in template.ComponentDeclarations)
                {
                    if (componentDec.IsInput)
                    {
                        if (componentDec.MissingInputBehavior.Value == MissingInputBehavior.Reset)
                        {
                            _resetGroups.Add(
                                new ResetGroupInfo
                                {
                                    GroupIndex = group,
                                    ComponentBuilder = componentDec.Builder,
                                }
                            );
                        }
                    }
                }
            }
        }

        public WorldAccessor Accessor
        {
            set
            {
                TrecsDebugAssert.IsNull(_accessor);
                TrecsDebugAssert.IsNotNull(value);
                _accessor = value;
            }
        }

        internal void SetInputsAppliedSubject(SimpleSubject systemRegistrySubject)
        {
            TrecsDebugAssert.IsNull(_systemRegistryInputsAppliedEvent);
            _systemRegistryInputsAppliedEvent = systemRegistrySubject;
        }

        public bool HasInputFrame<T>(int frame, EntityHandle entityHandle)
            where T : unmanaged, IEntityComponent
        {
            var typeId = TypeId<T>.Value;

            if (!_componentTypeHelpersMap.TryGetValue(typeId, out var componentInfo))
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
            var typeHash = TypeId<T>.Value;

            if (_componentTypeHelpersMap.TryGetValue(typeHash, out var componentInfo))
            {
                var helper = (ComponentTypeHelper<T>)componentInfo.Helper;

                var key = new FrameEntityHandlePair(frame, entityHandle);
                if (helper.Values.ContainsKey(key))
                {
                    return ref helper.Values.GetValueByRef(key);
                }
            }

            throw TrecsDebugAssert.CreateException(
                "Input not found for frame {0} and entity {1}",
                frame,
                entityHandle
            );
        }

        public bool TryGetInput<T>(int frame, EntityHandle entityHandle, out T value)
            where T : unmanaged, IEntityComponent
        {
            var typeHash = TypeId<T>.Value;

            if (_componentTypeHelpersMap.TryGetValue(typeHash, out var componentInfo))
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
            var typeHash = TypeId<T>.Value;

            if (!_componentTypeHelpersMap.TryGetValue(typeHash, out var componentInfo))
            {
                componentInfo = new() { Helper = new ComponentTypeHelper<T>() };

                _componentTypeHelpersMap.Add(typeHash, componentInfo);
            }

            var key = new FrameEntityHandlePair(frame, entityHandle);
            var values = ((ComponentTypeHelper<T>)componentInfo.Helper).Values;

            if (!existsOk)
            {
                TrecsDebugAssert.That(
                    !values.ContainsKey(key),
                    "Input already exists for frame {0} and entity {1}",
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
            foreach (var pair in _componentTypeHelpersMap)
            {
                var info = pair.Value;
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
                    TrecsDebugAssert.That(wasRemoved);

                    foreach (var entityHandle in entityHandleSet)
                    {
                        info.Helper.Remove(new(key, entityHandle));
                    }
                    entityHandleSet.Clear();

                    DespawnEntityHandleSet(info, entityHandleSet);
                }
            }

            _inputUniqueHeap.ClearAtOrAfterFrame(frame);
            _inputSharedHeap.ClearAtOrAfterFrame(frame);
            _inputNativeSharedHeap.ClearAtOrAfterFrame(frame);
            _inputNativeUniqueHeap.ClearAtOrAfterFrame(frame);
        }

        public void ClearAllInputs()
        {
            _log.Trace("Clearing all inputs");

            foreach (var pair in _componentTypeHelpersMap)
            {
                var info = pair.Value;
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

            _inputUniqueHeap.ClearAll();
            _inputSharedHeap.ClearAll();
            _inputNativeSharedHeap.ClearAll();
            _inputNativeUniqueHeap.ClearAll();
        }

        IterableHashSet<EntityHandle> SpawnEntityHandleValueIdSet(ComponentTypeInfo info)
        {
            if (info.EntityHandleSetPool.Count > 0)
            {
                var result = info.EntityHandleSetPool.Pop();
                TrecsDebugAssert.That(result.IsEmpty);
                return result;
            }

            using (TrecsProfiling.Start("Allocating new IterableHashSet<EntityHandle>"))
            {
                return new IterableHashSet<EntityHandle>(8);
            }
        }

        void DespawnEntityHandleSet(
            ComponentTypeInfo info,
            IterableHashSet<EntityHandle> entityHandleSet
        )
        {
            TrecsDebugAssert.That(entityHandleSet.IsEmpty);
            info.EntityHandleSetPool.Push(entityHandleSet);
        }

        public void ClearInputsBeforeOrAt(int frame)
        {
            _log.Trace("ClearInputsBeforeOrAt frame {0}", frame);

            foreach (var pair in _componentTypeHelpersMap)
            {
                var info = pair.Value;
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
                    TrecsDebugAssert.That(wasRemoved);

                    foreach (var entityHandle in entityHandleSet)
                    {
                        info.Helper.Remove(new(key, entityHandle));
                    }
                    entityHandleSet.Clear();

                    DespawnEntityHandleSet(info, entityHandleSet);
                }
            }

            _inputUniqueHeap.ClearAtOrBeforeFrame(frame);
            _inputSharedHeap.ClearAtOrBeforeFrame(frame);
            _inputNativeSharedHeap.ClearAtOrBeforeFrame(frame);
            _inputNativeUniqueHeap.ClearAtOrBeforeFrame(frame);
        }

        // Note: these Serialize/Deserialize methods are used by the recording system
        // for deterministic replay, not by snapshot serialization. Snapshots don't need
        // input queue state because the component values themselves (which include the
        // last-applied input for Retain components) are already captured in the
        // WorldStateSerializer snapshot.
        public void Serialize(ISerializationWriter writer)
        {
            writer.Write("NumHelpers", _componentTypeHelpersMap.Count);
            long bytesStart;

            foreach (var pair in _componentTypeHelpersMap)
            {
                var info = pair.Value;
                bytesStart = writer.NumBytesWritten;
                writer.Write("ComponentType", info.Helper.ComponentType);
                TrecsDebugAssert.IsNotNull(info.Helper);
                info.Helper.SerializeValues(writer);

                writer.Write("NumFrameEntries", info.FrameEntries.Count);

                foreach (var (frame, entityHandleSet) in info.FrameEntries)
                {
                    writer.Write("Frame", frame);
                    writer.Write<IterableHashSet<EntityHandle>>("Erefs", entityHandleSet);
                }

                _log.Debug(
                    "Serialized {0:0.00} kb for type {1} ({2} frames)",
                    (writer.NumBytesWritten - bytesStart) / 1024f,
                    info.Helper.ComponentType,
                    info.FrameEntries.Count
                );
            }

            bytesStart = writer.NumBytesWritten;
            _inputUniqueHeap.Serialize(writer);
            _log.Debug(
                "Serialized {0:0.00} kb for InputUniqueHeap",
                (writer.NumBytesWritten - bytesStart) / 1024f
            );

            bytesStart = writer.NumBytesWritten;
            _inputSharedHeap.Serialize(writer);
            _log.Debug(
                "Serialized {0:0.00} kb for InputSharedHeap",
                (writer.NumBytesWritten - bytesStart) / 1024f
            );

            bytesStart = writer.NumBytesWritten;
            _inputNativeSharedHeap.Serialize(writer);
            _log.Debug(
                "Serialized {0:0.00} kb for InputNativeSharedHeap",
                (writer.NumBytesWritten - bytesStart) / 1024f
            );

            bytesStart = writer.NumBytesWritten;
            _inputNativeUniqueHeap.Serialize(writer);
            _log.Debug(
                "Serialized {0:0.00} kb for InputNativeUniqueHeap",
                (writer.NumBytesWritten - bytesStart) / 1024f
            );
        }

        public void Deserialize(ISerializationReader reader)
        {
            _log.Trace("Deserializing EntityInputQueue");

            foreach (var pair in _componentTypeHelpersMap)
            {
                var existingInfo = pair.Value;
                foreach (var entityHandleSet in existingInfo.FrameEntries)
                {
                    entityHandleSet.Value.Clear();
                    existingInfo.EntityHandleSetPool.Push(entityHandleSet.Value);
                }
                existingInfo.FrameEntries.Clear();
                existingInfo.Helper.Clear();
            }

            var numHelpers = reader.Read<int>("NumHelpers");

            for (int i = 0; i < numHelpers; i++)
            {
                var componentType = reader.Read<Type>("ComponentType");
                var typeHash = TypeId.FromType(componentType);

                if (!_componentTypeHelpersMap.TryGetValue(typeHash, out var info))
                {
                    info = new ComponentTypeInfo();
                    _componentTypeHelpersMap.Add(typeHash, info);
                }

                if (info.Helper == null)
                {
                    info.Helper = CreateHelperForType(componentType);
                }
                info.Helper.DeserializeValues(reader);
                TrecsDebugAssert.IsNotNull(info.Helper);

                var numFrameEntries = reader.Read<int>("NumFrameEntries");

                for (int k = 0; k < numFrameEntries; k++)
                {
                    var frame = reader.Read<int>("Frame");
                    var entityHandleSet = SpawnEntityHandleValueIdSet(info);
                    reader.ReadInPlace<IterableHashSet<EntityHandle>>("Erefs", entityHandleSet);

                    info.FrameEntries.Add(frame, entityHandleSet);
                }
            }

            _inputUniqueHeap.Deserialize(reader);
            _inputSharedHeap.Deserialize(reader);
            _inputNativeSharedHeap.Deserialize(reader);
            _inputNativeUniqueHeap.Deserialize(reader);
        }

        internal void ResetInputs<T>(GroupIndex group)
            where T : unmanaged, IEntityComponent
        {
            _accessor.ComponentBuffer<T>(group).Write.Clear();
        }

        internal void ApplyInputs<T>(
            IterableHashSet<EntityHandle> entityHandles,
            int frame,
            NativeIterableDictionary<FrameEntityHandlePair, T> values
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
                        "Attempted to apply input for non-existing entity {0}",
                        entityHandle
                    );
                    continue;
                }

                var valuesKey = new FrameEntityHandlePair(frame, entityHandle);
                ref readonly var desiredValue = ref values.GetValueByRef(valuesKey);

                var entityIndex = entityHandle.ToIndex(world);

                if (!entityIndex.TryComponent<T>(world, out var component))
                {
                    _removeQueueBuffer.Add(entityHandle);
                    values.Remove(valuesKey);

                    _log.Warning(
                        "Found input with type {0} for entity but entity doesn't exist. Discarding input",
                        typeof(T)
                    );
                    continue;
                }

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
            TrecsDebugAssert.That(!_historyLockers.Contains(locker));
            _historyLockers.Add(locker);
        }

        public void RemoveHistoryLocker(IInputHistoryLocker locker)
        {
            var wasRemoved = _historyLockers.Remove(locker);
            TrecsDebugAssert.That(wasRemoved);
        }

        public int GetMaxClearFrame(int currentFixedFrame)
        {
            return CalculateMaxClearFrame(currentFixedFrame);
        }

        int CalculateMaxClearFrame(int currentFixedFrame)
        {
            // We need to do -1 for the heap allocated inputs
            // since we don't want to dispose those until next frame, since they could be used this frame
            int maxClearFrame = currentFixedFrame - 1;

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

        internal void ApplyInputs(int currentFixedFrame)
        {
            using (TrecsProfiling.Start("Resetting Input Values"))
            {
                foreach (var info in _resetGroups)
                {
                    info.ComponentBuilder.ResetInputs(this, info.GroupIndex);
                }
            }

#if TRECS_IS_PROFILING
            using (TrecsProfiling.Start("Applying Inputs"))
#endif
            {
                foreach (var pair in _componentTypeHelpersMap)
                {
                    var info = pair.Value;
                    if (info.FrameEntries.TryGetValue(currentFixedFrame, out var entityHandles))
                    {
                        info.Helper.ApplyInputs(this, entityHandles, currentFixedFrame);

                        if (entityHandles.IsEmpty)
                        {
                            info.FrameEntries.RemoveMustExist(currentFixedFrame);
                            DespawnEntityHandleSet(info, entityHandles);
                        }
                    }
                }
            }

#if TRECS_IS_PROFILING
            using (TrecsProfiling.Start("Clearing old input"))
#endif
            {
                _maxClearFrame = CalculateMaxClearFrame(currentFixedFrame);

                ClearInputsBeforeOrAt(_maxClearFrame);
            }

            _systemRegistryInputsAppliedEvent?.Invoke();
        }

        public void Dispose()
        {
            foreach (var pair in _componentTypeHelpersMap)
            {
                pair.Value.Helper.Dispose();
            }
            _componentTypeHelpersMap.Clear();

            // Note: inputs-applied event is owned by EventsManager, not EntityInputQueue
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
                return unchecked((int)math.hash(new int2(Frame, EntityHandle.GetHashCode())));
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

        class ResetGroupInfo
        {
            public GroupIndex GroupIndex;
            public IComponentBuilder ComponentBuilder;
        }

        internal interface IComponentTypeHelper : IDisposable
        {
            void Remove(FrameEntityHandlePair key);

            bool ContainsKey(FrameEntityHandlePair key);

            Type ComponentType { get; }

            void Clear();

            void ApplyInputs(
                EntityInputQueue entityInputQueue,
                IterableHashSet<EntityHandle> entityHandles,
                int frame
            );

            void SerializeValues(ISerializationWriter writer);
            void DeserializeValues(ISerializationReader reader);
        }

        internal class ComponentTypeHelper<T> : IComponentTypeHelper
            where T : unmanaged, IEntityComponent
        {
            public NativeIterableDictionary<FrameEntityHandlePair, T> Values = new(
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
                TrecsDebugAssert.That(wasRemoved);
            }

            public void ApplyInputs(
                EntityInputQueue entityInputQueue,
                IterableHashSet<EntityHandle> entityHandles,
                int frame
            )
            {
                entityInputQueue.ApplyInputs<T>(entityHandles, frame, Values);
            }

            public void Clear()
            {
                Values.Clear();
            }

            public void SerializeValues(ISerializationWriter writer)
            {
                Values.SerializeValues(writer);
            }

            public void DeserializeValues(ISerializationReader reader)
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
            public readonly IterableDictionary<int, IterableHashSet<EntityHandle>> FrameEntries =
                new();
            public readonly Stack<IterableHashSet<EntityHandle>> EntityHandleSetPool = new();
        }
    }
}
