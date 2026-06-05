using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// A compact fingerprint of every world-schema invariant the binary
    /// snapshot/recording wire format depends on. Saved into each snapshot's
    /// metadata and each recording bundle's header, and validated on load —
    /// so a snapshot saved against one schema fails loudly (and explains
    /// itself) when loaded into a world whose schema has since changed,
    /// instead of cascading into a misaligned binary read.
    ///
    /// <para>
    /// Split into sub-hashes so a mismatch can report <i>which aspect</i> of
    /// the schema diverged. Each is a 64-bit xxHash of a canonical byte
    /// stream:
    /// <list type="bullet">
    /// <item><see cref="GroupsHash"/> — every group in <see cref="GroupIndex"/>
    /// order: its tag-set identity, plus its ordered component types with
    /// their byte sizes, a source-gen-emitted field-layout hash, and effective
    /// variable-update-only flags. Changes when a component or tag is
    /// added/removed/renamed on a template, a component struct's size changes,
    /// a component's fields are reordered without changing its size, template
    /// registration order changes, or a <c>[VariableUpdateOnly]</c> flag is
    /// toggled.</item>
    /// <item><see cref="SetsHash"/> — the registered entity sets in
    /// registration order with their per-set group registrations.</item>
    /// <item><see cref="CustomSerializersHash"/> — the set of component types
    /// with a registered <see cref="IComponentArraySerializer{T}"/> (a custom
    /// serializer changes that component array's wire format).</item>
    /// <item><see cref="CustomSectionsHash"/> — the registered
    /// <see cref="ICustomWorldStateSection"/> names in registration order
    /// (each section appends its own block to the world-state stream, so the
    /// set and order are part of the wire format).</item>
    /// </list>
    /// The loaded <i>system list</i> is deliberately not covered: per-system
    /// paused state is serialized sparse and by system identity, so system
    /// add/remove/reorder keeps old snapshots loadable. Behavioral changes to
    /// simulation code are a replay-determinism concern, surfaced at runtime
    /// by the recording desync checksums rather than rejected at load.
    /// </para>
    ///
    /// <para>
    /// Computed once per world (see <c>World.SchemaFingerprint</c>) and
    /// deterministic across processes and platforms for the same schema:
    /// type identities hash by type name and sizes come from the component
    /// struct layouts.
    /// </para>
    /// </summary>
    // Serialized via blit (raw struct bytes) into snapshot metadata and bundle
    // headers. Adding, removing, or reordering fields changes the wire format —
    // bump SerializationHeaderUtil.FormatVersion and
    // TrecsConstants.CurrentBundleFormatVersion alongside any layout change.
    [TypeId(847203951)]
    public readonly struct WorldSchemaFingerprint : IEquatable<WorldSchemaFingerprint>
    {
        public readonly ulong GroupsHash;
        public readonly ulong SetsHash;
        public readonly ulong CustomSerializersHash;
        public readonly ulong CustomSectionsHash;

        public WorldSchemaFingerprint(
            ulong groupsHash,
            ulong setsHash,
            ulong customSerializersHash,
            ulong customSectionsHash
        )
        {
            GroupsHash = groupsHash;
            SetsHash = setsHash;
            CustomSerializersHash = customSerializersHash;
            CustomSectionsHash = customSectionsHash;
        }

        public bool Equals(WorldSchemaFingerprint other)
        {
            return GroupsHash == other.GroupsHash
                && SetsHash == other.SetsHash
                && CustomSerializersHash == other.CustomSerializersHash
                && CustomSectionsHash == other.CustomSectionsHash;
        }

        public override bool Equals(object obj)
        {
            return obj is WorldSchemaFingerprint other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(
                GroupsHash,
                SetsHash,
                CustomSerializersHash,
                CustomSectionsHash
            );
        }

        public static bool operator ==(WorldSchemaFingerprint a, WorldSchemaFingerprint b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(WorldSchemaFingerprint a, WorldSchemaFingerprint b)
        {
            return !a.Equals(b);
        }

        public override string ToString()
        {
            return $"WorldSchemaFingerprint(Groups:{GroupsHash:X16} Sets:{SetsHash:X16} "
                + $"CustomSerializers:{CustomSerializersHash:X16} "
                + $"CustomSections:{CustomSectionsHash:X16})";
        }
    }
}

namespace Trecs.Internal
{
    /// <summary>
    /// Computes <see cref="WorldSchemaFingerprint"/>s from a live world, and
    /// renders the user-facing explanation when a persisted fingerprint does
    /// not match the live one. Runs exactly once per world, at the end of
    /// <c>World.Initialize</c> (after the component-array serializer registry
    /// seals) — never on the per-snapshot hot path.
    /// </summary>
    internal static class WorldSchemaFingerprintCalculator
    {
        // Independent seeds so two sections with coincidentally identical
        // canonical streams still produce distinct sub-hashes.
        const ulong GroupsSeed = 0x5343484D5F475250; // "SCHM_GRP"
        const ulong SetsSeed = 0x5343484D5F534554; // "SCHM_SET"
        const ulong CustomSerializersSeed = 0x5343484D5F435553; // "SCHM_CUS"
        const ulong CustomSectionsSeed = 0x5343484D5F534543; // "SCHM_SEC"

        public static WorldSchemaFingerprint Compute(World world)
        {
            return new WorldSchemaFingerprint(
                ComputeGroupsHash(world.WorldInfo),
                ComputeSetsHash(world.SetStore),
                ComputeCustomSerializersHash(world),
                ComputeCustomSectionsHash(world.CustomWorldStateSections)
            );
        }

        static ulong ComputeGroupsHash(WorldInfo worldInfo)
        {
            var hash = XxHash64Builder.Create(GroupsSeed);

            var allGroups = worldInfo.AllGroups;
            AddInt(ref hash, allGroups.Count);

            for (int i = 0; i < allGroups.Count; i++)
            {
                var group = GroupIndex.FromIndex(i);

                // The tag-set id is the XOR of the member tags' name-derived
                // TypeIds — order-independent, and it shifts when a tag is
                // added, removed, or renamed.
                AddInt(ref hash, worldInfo.ToTagSet(group).Id);

                // ComponentBuilders order is the wire order: deserialization
                // preallocates each group's component slots from it
                // (ComponentStore.PreallocateDBGroup), and serialization
                // walks the slots in that same insertion order.
                var template = worldInfo.GetResolvedTemplateForGroup(group);
                var builders = template.ComponentBuilders;
                AddInt(ref hash, builders.Length);

                foreach (var builder in builders)
                {
                    var componentType = builder.ComponentType;
                    // Name-derived id: catches add/remove/rename/reorder.
                    AddInt(ref hash, builder.TypeId.Value);
                    // Blit width: catches field-layout changes that alter the
                    // struct size.
                    AddInt(ref hash, UnsafeUtility.SizeOf(componentType));
                    // Compile-time field-layout hash (source-gen-emitted const):
                    // catches a same-size field reorder that the size check above
                    // misses, so a stale snapshot fails loudly rather than blitting
                    // bytes into the wrong fields. Components not run through
                    // source-gen (hand-written/external) have no const and fall
                    // back to size-only via a fixed sentinel — same behavior as
                    // before this hash existed.
                    AddUlong(ref hash, GetComponentLayoutHash(componentType));
                    // VUO components serialize as a count instead of a full
                    // array, so the effective flag is part of the wire shape.
                    var dec = template.GetComponentDeclaration(componentType);
                    AddByte(ref hash, template.IsVariableUpdateOnly(dec) ? (byte)1 : (byte)0);
                }
            }

            return hash.Digest();
        }

        static ulong ComputeSetsHash(SetStore setStore)
        {
            var hash = XxHash64Builder.Create(SetsSeed);

            var setIds = setStore.SetIds;
            AddInt(ref hash, setIds.Length);

            for (int i = 0; i < setIds.Length; i++)
            {
                var setId = setIds[i];
                AddInt(ref hash, setId.Value);

                // Per-set group registrations are part of the wire format:
                // SetStore.Deserialize resolves each serialized
                // group back into the set's registered-group entries.
                var registeredGroups = setStore.GetSet(setId)._registeredGroups;
                AddInt(ref hash, registeredGroups.Length);
                for (int k = 0; k < registeredGroups.Length; k++)
                {
                    AddInt(ref hash, registeredGroups[k].Index);
                }
            }

            return hash.Digest();
        }

        /// <summary>
        /// Hash of the set of component types that currently have a custom
        /// <see cref="IComponentArraySerializer{T}"/> registered <i>and</i>
        /// are declared on some resolved template. A serializer registered
        /// for a type outside the schema never affects the wire (the
        /// dispatcher is only consulted for component arrays that exist), so
        /// including it would only manufacture false incompatibilities.
        /// Order-independent (sorted by type id) because registration order
        /// is not meaningful.
        /// </summary>
        static ulong ComputeCustomSerializersHash(World world)
        {
            var hash = XxHash64Builder.Create(CustomSerializersSeed);

            // Scratch allocations are fine: this runs once per world.
            var schemaComponentTypes = new HashSet<Type>();
            foreach (var template in world.WorldInfo.ResolvedTemplates)
            {
                foreach (var dec in template.ComponentDeclarations)
                {
                    schemaComponentTypes.Add(dec.ComponentType);
                }
            }

            var ids = new List<int>();
            foreach (
                var typeKey in world.ComponentArraySerializerRegistry.GetRegisteredComponentTypes()
            )
            {
                if (schemaComponentTypes.Contains(typeKey.Value))
                {
                    ids.Add(TypeId.FromType(typeKey.Value).Value);
                }
            }
            ids.Sort();

            AddInt(ref hash, ids.Count);
            foreach (var id in ids)
            {
                AddInt(ref hash, id);
            }

            return hash.Digest();
        }

        /// <summary>
        /// Hash of the registered <see cref="ICustomWorldStateSection"/>
        /// names, in registration order — order matters because it is the
        /// wire order of the stream's custom-sections block.
        /// </summary>
        static ulong ComputeCustomSectionsHash(CustomWorldStateSectionRegistry sections)
        {
            var hash = XxHash64Builder.Create(CustomSectionsSeed);

            AddInt(ref hash, sections.Count);
            for (int i = 0; i < sections.Count; i++)
            {
                AddUlong(ref hash, sections.GetEntry(i).NameHash);
            }

            return hash.Digest();
        }

        /// <summary>
        /// The user-facing explanation for a fingerprint mismatch: names the
        /// diverging sections and the schema changes that produce each, so
        /// "load failed" turns into "here is what changed and why the file
        /// can't be read".
        /// </summary>
        public static string BuildMismatchMessage(
            string payloadKind,
            WorldSchemaFingerprint saved,
            WorldSchemaFingerprint current
        )
        {
            var sb = new StringBuilder();
            sb.Append("This ")
                .Append(payloadKind)
                .Append(
                    " was saved with a different world schema and cannot be loaded — "
                        + "the binary wire format depends on the schema matching exactly. "
                        + "Diverging aspect(s):"
                );

            if (saved.GroupsHash != current.GroupsHash)
            {
                sb.Append(
                    "\n - Groups/components: a component or tag type was added, removed, or "
                        + "renamed on a template; a component struct's size changed; a "
                        + "component's fields were reordered (same total size); template "
                        + "registration order changed; or a [VariableUpdateOnly] flag was toggled."
                );
            }
            if (saved.SetsHash != current.SetsHash)
            {
                sb.Append(
                    "\n - Entity sets: a set was added or removed, set registration order "
                        + "changed, or a set's group registrations changed."
                );
            }
            if (saved.CustomSerializersHash != current.CustomSerializersHash)
            {
                sb.Append(
                    "\n - Custom component-array serializers: the set of component types with "
                        + "a registered IComponentArraySerializer differs from save time."
                );
            }
            if (saved.CustomSectionsHash != current.CustomSectionsHash)
            {
                sb.Append(
                    "\n - Custom world-state sections: the registered ICustomWorldStateSection "
                        + "names (or their registration order) differ from save time."
                );
            }

            sb.Append("\nSaved:   ").Append(saved);
            sb.Append("\nCurrent: ").Append(current);
            sb.Append(
                payloadKind == "recording"
                    ? "\nRe-record against the current schema, or run a build whose schema "
                        + "matches the file."
                    : "\nRe-save the snapshot from a world built with the current schema, or "
                        + "run a build whose schema matches the file."
            );
            return sb.ToString();
        }

        // Name of the const the EntityComponentGenerator emits on each component
        // partial. Must stay in sync with
        // EntityComponentGenerator.ComponentLayoutHashFieldName.
        const string LayoutHashFieldName = "__TrecsComponentLayoutHash";

        // Sentinel mixed in for components without a generated layout-hash const
        // (hand-written or external types not run through source-gen). A fixed,
        // non-zero value keeps those components on size-only behavior — identical
        // to before this hash existed — while staying distinct from a real hash of
        // 0 only in the astronomically unlikely event a layout actually hashes to
        // this; that case merely loses the same-size guard for one component, never
        // produces a false mismatch.
        const ulong MissingLayoutHashSentinel = 0xA5A5A5A5A5A5A5A5UL;

        // Probes the generated layout-hash const via reflection. The whole
        // fingerprint is computed once per world (never on the per-snapshot hot
        // path), and the per-type reflection cost is trivial against that, so no
        // caching is needed even though a component type can recur across templates.
        static ulong GetComponentLayoutHash(Type componentType)
        {
            // Reading a literal const via reflection is IL2CPP-safe (it resolves to
            // metadata, not a field access) — unlike the field-layout walking that
            // was rejected for the runtime path. Generic component types expose the
            // const on the open generic definition's metadata too, so the closed
            // type's GetField finds it.
            var field = componentType.GetField(
                LayoutHashFieldName,
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic
            );
            if (field == null || !field.IsLiteral || field.FieldType != typeof(ulong))
            {
                return MissingLayoutHashSentinel;
            }
            var raw = field.GetRawConstantValue();
            return raw is ulong value ? value : MissingLayoutHashSentinel;
        }

        static void AddInt(ref XxHash64Builder hash, int value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            hash.Update(bytes);
        }

        static void AddByte(ref XxHash64Builder hash, byte value)
        {
            Span<byte> bytes = stackalloc byte[1];
            bytes[0] = value;
            hash.Update(bytes);
        }

        static void AddUlong(ref XxHash64Builder hash, ulong value)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
            hash.Update(bytes);
        }
    }
}
