using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Trecs.Collections;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal sealed class ComponentStore : IDisposable
    {
        readonly TrecsLog _log;

        // Per-group component arrays, indexed directly by GroupIndex.Index.
        // The outer array is sized at world build (one IterableDictionary per
        // group); the inner per-component IComponentArray slots are populated
        // lazily on first touch — usually the group's first AddEntity, or the
        // serializer's eager pre-materialize for snapshot/recording reads. A
        // direct array index replaces the old Dict<GroupIndex, ...> hash
        // lookup on the hot path.
        readonly IterableDictionary<TypeId, IComponentArray>[] _groupEntityComponentsMaps;

        bool _configurationFrozen;

        public ComponentStore(TrecsLog log, int groupCount)
        {
            _log = log;
            _groupEntityComponentsMaps = new IterableDictionary<TypeId, IComponentArray>[
                groupCount
            ];
            for (int i = 0; i < groupCount; i++)
            {
                _groupEntityComponentsMaps[i] = new IterableDictionary<TypeId, IComponentArray>();
            }
        }

        public int GroupCount => _groupEntityComponentsMaps.Length;

        public bool ConfigurationFrozen => _configurationFrozen;

        public void FreezeConfiguration()
        {
            _configurationFrozen = true;
        }

        /// <summary>
        /// Returns the per-component inner dictionary for a group. Every valid
        /// GroupIndex has a pre-allocated (initially empty) inner dictionary
        /// populated by <see cref="PreallocateDBGroup"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IterableDictionary<TypeId, IComponentArray> GetDBGroup(GroupIndex groupId)
        {
            return _groupEntityComponentsMaps[groupId.Index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IComponentArray GetOrAddTypeSafeDictionary(
            GroupIndex groupId,
            IterableDictionary<TypeId, IComponentArray> groupPerComponentType,
            TypeId typeId,
            IComponentArray fromDictionary
        )
        {
            // Per-group component-array slots are materialized lazily on first
            // touch — both during world build (no eager warm-up) and during
            // entity submission (a fresh group's slots show up when entities
            // first land in it). The freeze flag is intentionally not checked
            // here; it only signals "no more groups" upstream, not "no more
            // slots on existing groups."
            if (
                !groupPerComponentType.TryGetValue(typeId, out IComponentArray toEntitiesDictionary)
            )
            {
                toEntitiesDictionary = fromDictionary.Create();
                groupPerComponentType.Add(typeId, toEntitiesDictionary);
            }

            return toEntitiesDictionary;
        }

        /// <summary>
        /// Eagerly materialize the per-group component-array slots for a given
        /// template's builders. Component slots are normally lazy (created on
        /// first entity), but serialization read paths need them populated up
        /// front so the in-memory layout matches the on-wire shape.
        /// </summary>
        public void PreallocateDBGroup(
            GroupIndex groupId,
            int size,
            IComponentBuilder[] entityComponentsToBuild
        )
        {
            var numberOfEntityComponents = entityComponentsToBuild.Length;
            IterableDictionary<TypeId, IComponentArray> group = GetDBGroup(groupId);
            group.EnsureCapacity(numberOfEntityComponents);

            for (var index = 0; index < numberOfEntityComponents; index++)
            {
                var entityComponentBuilder = entityComponentsToBuild[index];
                var entityComponentType = entityComponentBuilder.TypeId;

                var components = group.GetOrAdd(
                    entityComponentType,
                    () => entityComponentBuilder.CreateDictionary(size)
                );

                entityComponentBuilder.Preallocate(components, size);
            }

            _log.Trace(
                "Initialized group {0} with {1} component arrays",
                groupId,
                numberOfEntityComponents
            );
        }

        /// <summary>
        /// Writes every group's component arrays to the snapshot stream. The
        /// wire format lives here, next to the storage layout it mirrors;
        /// <see cref="WorldStateSerializer"/> only owns section ordering.
        /// </summary>
        public void Serialize(
            ISerializationWriter writer,
            WorldInfo worldInfo,
            ComponentArraySerializerRegistry serializerRegistry
        )
        {
            var numItems = _groupEntityComponentsMaps.Length;

            writer.Write("Count", numItems);

            for (int i = 0; i < numItems; i++)
            {
                writer.PushScope("Group{0}", i);
                var bytesBefore = writer.NumBytesWritten;

                var group = GroupIndex.FromIndex(i);
                writer.Write("Group", worldInfo.ToTagSet(group));

                var subMap = _groupEntityComponentsMaps[i];

                var numComponents = subMap.Count;

                // Component-array slots are materialized lazily on first
                // entity. A group can end up with populated slots but zero
                // entities (e.g. after deserialization preallocates the
                // schema). This materialization state is a non-observable
                // implementation detail — treat an all-empty group
                // identically to a never-materialized one so serialized
                // bytes stay stable across recording/playback.
                if (numComponents > 0)
                {
                    var allEmpty = true;
                    for (int k = 0; k < numComponents; k++)
                    {
                        if (subMap.UnsafeValues[k].Count > 0)
                        {
                            allEmpty = false;
                            break;
                        }
                    }

                    if (allEmpty)
                    {
                        numComponents = 0;
                    }
                }

                writer.Write("NumComponents", numComponents);

                for (int k = 0; k < numComponents; k++)
                {
                    writer.PushScope("Component{0}", k);
                    TypeId componentId = subMap.UnsafeKeys[k].Key;
                    writer.Write("TypeId", componentId);

                    IComponentArray componentArray = subMap.UnsafeValues[k];

                    if (ShouldSkip(worldInfo, group, componentId))
                    {
                        writer.Write("Count", componentArray.Count);
                    }
                    else
                    {
                        WriteComponentArray(componentArray, writer, serializerRegistry);
                    }
                    writer.PopScope();
                }

                _log.Trace(
                    "GroupIndex {0} serialized in {1} kb",
                    group,
                    (writer.NumBytesWritten - bytesBefore) / 1024f
                );
                writer.PopScope();
            }
        }

        public void Deserialize(
            ISerializationReader reader,
            WorldInfo worldInfo,
            ComponentArraySerializerRegistry serializerRegistry
        )
        {
            var numItems = reader.Read<int>("Count");
            TrecsDebugAssert.That(numItems >= 0);

            TrecsDebugAssert.IsEqual(_groupEntityComponentsMaps.Length, numItems);

            for (int i = 0; i < numItems; i++)
            {
                var tagSet = reader.Read<TagSet>("Group");
                var group = worldInfo.ToGroupIndex(tagSet);

                TrecsDebugAssert.IsEqual(GroupIndex.FromIndex(i), group);

                var subMap = _groupEntityComponentsMaps[i];

                var numComponents = reader.Read<int>("NumComponents");

                // Per-group component slots are normally lazy (created on
                // first entity). Snapshot/recording reads need them in place
                // so the wire-format walk lines up with the in-memory map —
                // materialize them eagerly here for the group we're about
                // to populate.
                if (numComponents > 0 && subMap.Count == 0)
                {
                    var template = worldInfo.GetResolvedTemplateForGroup(group);
                    PreallocateDBGroup(group, 0, template.ComponentBuilders);
                }
                else if (numComponents == 0 && subMap.Count > 0)
                {
                    // Snapshot pre-dates this group's first entity, but the
                    // live world has since materialized it. Empty the arrays
                    // so the group is logically empty at the restored frame.
                    // Slots themselves stay (queries iterate Count, so an
                    // empty-arrays group is observationally identical to a
                    // never-materialized one) and there's nothing more to
                    // read for this group on the wire.
                    for (int k = 0; k < subMap.Count; k++)
                    {
                        subMap.UnsafeValues[k].Clear();
                    }
                    continue;
                }

                if (subMap.Count != numComponents)
                {
                    // SerializationException (not TrecsDebugAssert) because the loop
                    // below indexes subMap by the snapshot's component count and then
                    // blits raw bytes — a mismatch must fail loud in release builds
                    // too, before the unsafe reads, not after they've corrupted state.
                    throw new SerializationException(
                        $"Unexpected number of components for group {group}. Expected "
                            + $"{numComponents} (from snapshot), got {subMap.Count} (in memory)."
                    );
                }

                for (int k = 0; k < numComponents; k++)
                {
                    var componentId = reader.Read<TypeId>("TypeId");
                    if (subMap.UnsafeKeys[k].Key != componentId)
                    {
                        // Release-on for the same reason as the count check above:
                        // blitting one component type's bytes into another's array
                        // silently corrupts the simulation even when the sizes line up.
                        throw new SerializationException(
                            $"Component type mismatch for group {group} at slot {k}: "
                                + $"snapshot has {componentId}, world has {subMap.UnsafeKeys[k].Key}."
                        );
                    }

                    if (ShouldSkip(worldInfo, group, componentId))
                    {
                        var count = reader.Read<int>("Count");
                        var arr = subMap.UnsafeValues[k];
                        arr.ResetToDefaultValuesWithCount(count);
                        continue;
                    }

                    ReadComponentArray(subMap.UnsafeValues[k], reader, serializerRegistry);
                }
            }
        }

        // Variable-update-only components are visual-sync scratch state — not
        // part of the deterministic simulation, so snapshots persist only the
        // array count and reset values to default on load.
        static bool ShouldSkip(WorldInfo worldInfo, GroupIndex group, TypeId componentId)
        {
            var componentType = TypeId.ToType(componentId);
            var template = worldInfo.GetResolvedTemplateForGroup(group);
            var componentDec = template.GetComponentDeclaration(componentType);
            return template.IsVariableUpdateOnly(componentDec);
        }

        static void WriteComponentArray(
            IComponentArray array,
            ISerializationWriter writer,
            ComponentArraySerializerRegistry serializerRegistry
        )
        {
            var count = array.Count;
            writer.Write("Count", count);

            if (serializerRegistry.TryGetDispatcher(array.ComponentType, out var dispatcher))
            {
                writer.WriteBit(true);
                dispatcher.Serialize(array, writer);
                return;
            }

            writer.WriteBit(false);
            if (count > 0)
            {
                unsafe
                {
                    writer.BlitWriteRawBytes(
                        "Values",
                        array.GetUnsafePtr(),
                        array.ElementSize * count
                    );
                }
            }
        }

        static void ReadComponentArray(
            IComponentArray array,
            ISerializationReader reader,
            ComponentArraySerializerRegistry serializerRegistry
        )
        {
            var count = reader.Read<int>("Count");
            bool isCustom = reader.ReadBit();
            if (isCustom)
            {
                var ok = serializerRegistry.TryGetDispatcher(
                    array.ComponentType,
                    out var dispatcher
                );
                TrecsDebugAssert.That(
                    ok,
                    "Stream marks a custom serializer for component type {0} but none is registered on this world",
                    array.ComponentType
                );
                dispatcher.Deserialize(array, count, reader);
                return;
            }

            array.Clear();
            if (count > 0)
            {
                array.EnsureCapacity(count);
                unsafe
                {
                    reader.BlitReadRawBytes(
                        "Values",
                        array.GetUnsafePtr(),
                        array.ElementSize * count
                    );
                }
            }
            array.SetCount(count);
        }

        public void Dispose()
        {
            for (int i = 0; i < _groupEntityComponentsMaps.Length; i++)
            {
                var group = _groupEntityComponentsMaps[i];
                foreach (var entityList in group)
                {
                    entityList.Value.Dispose();
                }
                group.Clear();
            }
        }
    }
}
