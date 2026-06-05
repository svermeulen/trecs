using System.Collections.Generic;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// A game-defined section of the full world-state stream, for
    /// deterministic state that lives outside the ECS (e.g. a scripting VM's
    /// internal state). Registered sections are serialized into every
    /// snapshot <see cref="WorldStateSerializer"/> produces — so rewind
    /// keyframes, recordings, save files, and desync checksums all cover the
    /// section's data automatically, on every path that captures world state
    /// (including the editor rewind tooling, which constructs its own
    /// <see cref="WorldStateSerializer"/>).
    ///
    /// <para>
    /// Register via <c>WorldBuilder.RegisterCustomWorldStateSection</c> or
    /// <see cref="World.CustomWorldStateSections"/> before
    /// <c>World.Initialize()</c>. <see cref="Serialize"/> and
    /// <see cref="Deserialize"/> must consume exactly mirrored wire data —
    /// drift is caught by the per-section guard byte that
    /// <see cref="WorldStateSerializer"/> writes after each section.
    /// </para>
    /// </summary>
    public interface ICustomWorldStateSection
    {
        void Serialize(ISerializationWriter writer);
        void Deserialize(ISerializationReader reader);
    }

    /// <summary>
    /// Registry of <see cref="ICustomWorldStateSection"/>s appended to the
    /// world-state stream after the built-in sections, in registration order.
    ///
    /// Owned by <see cref="World"/>; access via
    /// <see cref="World.CustomWorldStateSections"/>. Registrations are part
    /// of the world schema (they change the snapshot wire format and the
    /// <see cref="WorldSchemaFingerprint"/>), so the registry seals at
    /// <see cref="World.Initialize"/> — register via
    /// <c>WorldBuilder.RegisterCustomWorldStateSection</c> or between
    /// <c>Build()</c> and <c>Initialize()</c>.
    /// </summary>
    public sealed class CustomWorldStateSectionRegistry
    {
        internal readonly struct Entry
        {
            public readonly string Name;

            // Name-derived and stable across sessions/platforms — the
            // section's wire identity. Written before the section's payload
            // so a registration mismatch is reported by name instead of
            // cascading as a misaligned read.
            public readonly ulong NameHash;

            public readonly ICustomWorldStateSection Section;

            public Entry(string name, ulong nameHash, ICustomWorldStateSection section)
            {
                Name = name;
                NameHash = nameHash;
                Section = section;
            }
        }

        // Registration order is the wire order, so a List (not a dictionary)
        // is the source of truth.
        readonly List<Entry> _entries = new();

        bool _isSealed;

        public int Count => _entries.Count;

        /// <summary>
        /// The registration name of the section at <paramref name="index"/>
        /// (registration order — which is also the wire order). For
        /// logging/tooling; the section instances themselves are not exposed.
        /// </summary>
        public string GetSectionName(int index)
        {
            return _entries[index].Name;
        }

        internal Entry GetEntry(int index)
        {
            return _entries[index];
        }

        /// <summary>
        /// Called by <c>World.Initialize</c>: registrations define the
        /// snapshot wire format, and the world's schema fingerprint is
        /// computed once at that point, so later mutation would change the
        /// wire format mid-session — making snapshots taken earlier in the
        /// same session (e.g. rewind keyframes) unloadable.
        /// </summary>
        internal void Seal()
        {
            _isSealed = true;
        }

        /// <summary>
        /// Register <paramref name="section"/> under <paramref name="name"/>.
        /// The name is the section's stable wire identity (hashed into the
        /// stream and the schema fingerprint), so treat renames like any
        /// other wire format change. Must be called before
        /// <c>World.Initialize</c>; throws if the name is already registered.
        /// </summary>
        public void Register(string name, ICustomWorldStateSection section)
        {
            ThrowIfSealed();
            TrecsAssert.That(
                !string.IsNullOrEmpty(name),
                "Custom world-state section name must be non-empty"
            );
            TrecsAssert.That(section != null, "Custom world-state section must be non-null");

            var nameHash = CollisionResistantHashCalculator.ComputeXxHash64(name);

            // Release-safe duplicate guard: a duplicate name would make the
            // wire stream ambiguous, which the debug-stripped assert variant
            // could miss in release builds.
            foreach (var entry in _entries)
            {
                TrecsAssert.That(
                    entry.NameHash != nameHash,
                    "Custom world-state section {0} is already registered (or collides with "
                        + "{1} on name hash)",
                    name,
                    entry.Name
                );
            }

            _entries.Add(new Entry(name, nameHash, section));
        }

        // Release-safe (TrecsAssert, not the stripped debug variant): this is
        // a setup-time call with zero hot-path cost, and a mid-session wire
        // format change is exactly the kind of corruption the schema
        // fingerprint exists to prevent.
        void ThrowIfSealed()
        {
            TrecsAssert.That(
                !_isSealed,
                "CustomWorldStateSectionRegistry is sealed — registrations are part of the "
                    + "world schema and must complete before World.Initialize(). Use "
                    + "WorldBuilder.RegisterCustomWorldStateSection, or mutate the registry "
                    + "between Build() and Initialize()."
            );
        }
    }
}
