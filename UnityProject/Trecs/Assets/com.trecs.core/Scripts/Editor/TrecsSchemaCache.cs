using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Trecs.Internal;
using UnityEditor;
using UnityEngine;

namespace Trecs
{
    /// <summary>
    /// On-disk snapshot of a Trecs <see cref="World"/>'s static schema —
    /// templates, components, accessors/systems, sets — plus a header with
    /// the source world name and timestamp. Written by
    /// <see cref="TrecsSchemaCache"/> on world registration / unregistration
    /// and read back by <see cref="TrecsHierarchyWindow"/> when no live world
    /// is available, so the user can browse the ECS shape between play
    /// sessions. The schema is intentionally lossy — entity instances and
    /// runtime counts are not captured.
    /// </summary>
    [Serializable]
    public class TrecsSchema
    {
        public string SchemaVersion = "1";
        public string WorldName;
        public string SavedAtIso;
        public List<TrecsSchemaTemplate> Templates = new();
        public List<TrecsSchemaComponentType> ComponentTypes = new();
        public List<TrecsSchemaSystem> Systems = new();
        public List<TrecsSchemaAccessor> ManualAccessors = new();
        public List<TrecsSchemaSet> Sets = new();
        public List<TrecsSchemaTag> Tags = new();

        // Access data captured from the live TrecsAccessTracker at save time.
        // One entry per component type that's been read or written by at
        // least one accessor since the world started — empty if the tracker
        // didn't run (e.g. world disposed without any system ticks). Entries
        // here accumulate across play sessions via TrecsSchemaCache merge.
        public List<TrecsSchemaAccessInfo> Access = new();

        // Tags touched per accessor, derived at save time by mapping each
        // accessor's GetGroupsTouchedBy through WorldInfo.GetGroupTags. Lets
        // cache mode show "Tags touched" without a live tracker / world. Same
        // accumulation semantics as Access.
        public List<TrecsSchemaTagsTouchedInfo> TagsTouched = new();

        // Per-accessor record of which templates the accessor performed
        // structural changes on (Add / Remove / Move). Move flags both the
        // source and destination templates of each operation. Same
        // accumulation semantics as Access / TagsTouched.
        public List<TrecsSchemaStructuralInfo> Structural = new();
    }

    [Serializable]
    public class TrecsSchemaTemplate
    {
        public string DebugName;
        public bool IsResolved;
        public List<string> AllTagNames = new();
        public List<string> BaseTemplateNames = new();

        // Inverse of BaseTemplateNames — every template that extends this
        // one. Computed at schema build time by walking AllBaseTemplates
        // across the world.
        public List<string> DerivedTemplateNames = new();

        // Union of direct + inherited (kept for back-compat with caches
        // written before the split). New code reads from the two specific
        // lists below; falls back to this if both are empty.
        public List<string> ComponentTypeNames = new();

        // Components declared directly on this template (template.Local
        // ComponentDeclarations). Synthesized companions of an interpolated
        // local (Interpolated<T>, InterpolatedPrevious<T>) currently land
        // in Inherited rather than Direct — minor classification quirk.
        public List<string> DirectComponentTypeNames = new();

        // Components resolved from base templates (everything in the
        // resolved set whose ComponentType isn't in the local declarations).
        public List<string> InheritedComponentTypeNames = new();
        public List<TrecsSchemaPartition> Partitions = new();
    }

    [Serializable]
    public class TrecsSchemaPartition
    {
        public List<string> TagNames = new();
    }

    [Serializable]
    public class TrecsSchemaComponentType
    {
        public string DisplayName;
        public string FullName;
        public List<TrecsSchemaField> Fields = new();
    }

    [Serializable]
    public class TrecsSchemaField
    {
        public string Name;
        public string TypeName;
    }

    [Serializable]
    public class TrecsSchemaSystem
    {
        public string DebugName;
        public string TypeName;
        public string TypeNamespace;
        public string Phase;

        // The accessor role this system runs under (Input / Fixed /
        // Variable / Unrestricted). Persisted as the enum's string name so
        // older snapshots without this field deserialize as empty
        // and the inspector falls back gracefully. Always set on new
        // snapshots — TrecsSchemaCache derives it from the live
        // WorldAccessor at save time.
        public string Role;
        public bool HasPriority;
        public int Priority;
        public List<string> DependsOnSystemDebugNames = new();

        // Inverse of DependsOnSystemDebugNames. Computed at schema build
        // time so cache mode doesn't need to walk every system per
        // inspector refresh.
        public List<string> DependentSystemDebugNames = new();
    }

    [Serializable]
    public class TrecsSchemaAccessor
    {
        public string DebugName;

        // The accessor role this manual accessor was created with
        // (Input / Fixed / Variable / Unrestricted). Persisted as the enum's
        // string name so older snapshots without this field deserialize
        // as empty and the inspector falls back gracefully.
        public string Role;

        // Source-file path + line where world.CreateAccessor(...) was
        // called. Captured via CallerFilePath/CallerLineNumber on the
        // public CreateAccessor entry points; empty/0 in release builds
        // and for accessors made via paths that don't propagate caller
        // info (system-owned ones use system metadata instead).
        public string CreatedAtFile;
        public int CreatedAtLine;
    }

    [Serializable]
    public class TrecsSchemaSet
    {
        public string DebugName;
        public int Id;
        public string TypeFullName;
        public string TypeNamespace;
        public List<string> TagNames = new();
    }

    [Serializable]
    public class TrecsSchemaTag
    {
        public string Name;
        public int Guid;
        public List<string> TemplateNames = new();
        public List<string> SetNames = new();
    }

    [Serializable]
    public class TrecsSchemaAccessInfo
    {
        // Display name as rendered in the hierarchy's Components section
        // (matches TrecsHierarchyWindow.ComponentTypeDisplayName).
        public string ComponentDisplayName;

        // Field names say "Systems" for back-compat — they hold accessor
        // debug names, which include any manually-created accessor whose
        // reads/writes the tracker has observed, not just ISystem instances.
        public List<string> ReadBySystems = new();
        public List<string> WrittenBySystems = new();
    }

    [Serializable]
    public class TrecsSchemaTagsTouchedInfo
    {
        public string AccessorDebugName;
        public List<string> TagNames = new();
    }

    [Serializable]
    public class TrecsSchemaStructuralInfo
    {
        public string AccessorDebugName;

        // Template DebugNames for the groups the accessor mutated. A move
        // contributes its source template to MovedTemplateNames *and* its
        // destination — there is no separate "moved-from" / "moved-to" split.
        public List<string> AddedTemplateNames = new();
        public List<string> RemovedTemplateNames = new();
        public List<string> MovedTemplateNames = new();
    }

    /// <summary>
    /// Static, editor-only writer/reader for <see cref="TrecsSchema"/>.
    /// Subscribes to <see cref="WorldRegistry"/> events on editor load and
    /// writes a snapshot every time a world is registered or unregistered,
    /// so the cache always reflects the most recent run. The cache lives
    /// under <see cref="TrecsPaths.InspectorSchema"/> (Unity's
    /// <c>Library/</c> tree, gitignored by default).
    /// </summary>
    [InitializeOnLoad]
    public static class TrecsSchemaCache
    {
        public static event Action SchemaSaved;

        // Snapshots from worlds that haven't been registered in a long
        // time are almost always stale (renamed test scenes, removed
        // installer worlds, etc.). Drop them at editor startup so the
        // inspector schema directory doesn't fill up forever.
        const int _staleSnapshotDays = 30;

        static TrecsSchemaCache()
        {
            TrecsEditorAccessorNames.Register("TrecsSchemaCache");
            WorldRegistry.WorldRegistered += OnWorldRegistered;
            // Save + cached-accessor teardown both run from the registry's
            // pre-clear hook, not WorldUnregistered directly: the access
            // tracker is wiped inside its own OnWorldUnregistered handler
            // and [InitializeOnLoad] subscription order isn't deterministic,
            // so without this we'd race the tracker's data away.
            TrecsAccessRegistry.WorldAccessTrackerWillClear += OnWorldAccessTrackerWillClear;
            PruneStaleSnapshots();
        }

        static void PruneStaleSnapshots()
        {
            try
            {
                var dir = GetSchemaDirectory();
                if (!Directory.Exists(dir))
                    return;
                var threshold = DateTime.UtcNow - TimeSpan.FromDays(_staleSnapshotDays);
                foreach (var path in Directory.GetFiles(dir, "*.json"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(path) < threshold)
                        {
                            File.Delete(path);
                        }
                    }
                    catch (Exception)
                    {
                        // Best-effort — file might be read-only / locked /
                        // gone underneath us. Skip and move on.
                    }
                }
            }
            catch (Exception)
            {
                // Directory enumeration failed (permissions, missing
                // parent). Not worth surfacing — startup-only cleanup.
            }
        }

        static void OnWorldRegistered(World world)
        {
            TrySave(world);
        }

        // Fires from TrecsAccessRegistry just before it tears down the live
        // tracker for `world`. The world is still alive (Unregister runs at
        // the top of World.Dispose, before _isDisposed is set), so Build can
        // walk WorldInfo and read the tracker normally. We then drop the
        // cached accessor here (rather than from a separate WorldUnregistered
        // handler) so the save-then-teardown ordering is unambiguous.
        static void OnWorldAccessTrackerWillClear(World world)
        {
            TrySave(world);
            _cachedAccessors.Remove(world);
        }

        public static string GetSchemaDirectory()
        {
            return TrecsPaths.InspectorSchema;
        }

        // Schema is keyed by sanitized world name on disk so the dropdown
        // can list each cached world independently. Two worlds with names
        // that collide after sanitization will overwrite each other — fine
        // in practice, debug names rarely clash that way.
        public static string GetSchemaPath(string worldName)
        {
            var safe = SanitizeForFileName(worldName ?? "world");
            return Path.Combine(GetSchemaDirectory(), safe + ".json");
        }

        public static bool TryLoadAll(out List<TrecsSchema> schemas)
        {
            schemas = new List<TrecsSchema>();
            try
            {
                var dir = GetSchemaDirectory();
                if (!Directory.Exists(dir))
                {
                    return false;
                }
                foreach (var path in Directory.GetFiles(dir, "*.json"))
                {
                    try
                    {
                        var text = File.ReadAllText(path);
                        var schema = JsonUtility.FromJson<TrecsSchema>(text);
                        if (schema != null)
                        {
                            schemas.Add(schema);
                        }
                    }
                    catch (Exception)
                    {
                        // Skip corrupt entries.
                    }
                }
                return schemas.Count > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool TryLoad(string worldName, out TrecsSchema schema)
        {
            schema = null;
            try
            {
                var path = GetSchemaPath(worldName);
                if (!File.Exists(path))
                {
                    return false;
                }
                var text = File.ReadAllText(path);
                schema = JsonUtility.FromJson<TrecsSchema>(text);
                return schema != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        static string SanitizeForFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(invalid, chars[i]) >= 0)
                {
                    chars[i] = '_';
                }
            }
            return new string(chars);
        }

        public static void TrySave(World world)
        {
            if (world == null || world.IsDisposed)
            {
                return;
            }
            try
            {
                var fresh = Build(world);
                if (TryLoad(fresh.WorldName, out var existing) && existing != null)
                {
                    MergeRuntimeData(existing, fresh);
                }
                var path = GetSchemaPath(fresh.WorldName);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonUtility.ToJson(fresh, prettyPrint: true));
                SchemaSaved?.Invoke();
            }
            catch (Exception)
            {
                // Best-effort: if a world is in an unusual state we'd rather
                // skip the snapshot than spam the console. Next world tick
                // will retry.
            }
        }

        /// <summary>
        /// Delete the on-disk snapshot for a single world. No-op if the file
        /// doesn't exist. Used by the hierarchy window's Clear-cache action
        /// when the user wants to drop accumulated runtime data.
        /// </summary>
        public static void Clear(string worldName)
        {
            try
            {
                var path = GetSchemaPath(worldName);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    SchemaSaved?.Invoke();
                }
            }
            catch (Exception)
            {
                // Best-effort.
            }
        }

        /// <summary>
        /// Delete every snapshot under the schema directory. Used when the
        /// user wants to wipe all accumulated runtime data across worlds.
        /// </summary>
        public static void ClearAll()
        {
            try
            {
                var dir = GetSchemaDirectory();
                if (!Directory.Exists(dir))
                {
                    return;
                }
                foreach (var path in Directory.GetFiles(dir, "*.json"))
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch (Exception)
                    {
                        // Best-effort per file.
                    }
                }
                SchemaSaved?.Invoke();
            }
            catch (Exception)
            {
                // Best-effort.
            }
        }

        /// <summary>
        /// Carries forward runtime-derived data (component access, tags
        /// touched) from a prior snapshot into the freshly-built one, so
        /// each play session adds to what we know rather than overwriting
        /// it. Static schema (templates, components, systems, accessors,
        /// sets, tags) is left untouched — fresh always wins for those,
        /// since they describe the current code's structure. Stale entries
        /// for renamed/removed accessors will linger here until
        /// ClearAll/Clear. Lists are re-sorted at end so the merged JSON is
        /// deterministic and git-diff-friendly.
        /// <para>
        /// Public so unit tests can drive the merge in isolation —
        /// <see cref="TrySave"/> is the only production caller.
        /// </para>
        /// </summary>
        public static void MergeRuntimeData(TrecsSchema existing, TrecsSchema fresh)
        {
            bool accessChanged = false;
            if (existing.Access != null && existing.Access.Count > 0)
            {
                var byComponent = new Dictionary<string, TrecsSchemaAccessInfo>(fresh.Access.Count);
                foreach (var entry in fresh.Access)
                {
                    if (entry.ComponentDisplayName != null)
                    {
                        byComponent[entry.ComponentDisplayName] = entry;
                    }
                }
                foreach (var prior in existing.Access)
                {
                    if (prior?.ComponentDisplayName == null)
                    {
                        continue;
                    }
                    if (byComponent.TryGetValue(prior.ComponentDisplayName, out var current))
                    {
                        accessChanged |= UnionInto(current.ReadBySystems, prior.ReadBySystems);
                        accessChanged |= UnionInto(
                            current.WrittenBySystems,
                            prior.WrittenBySystems
                        );
                    }
                    else
                    {
                        var copy = new TrecsSchemaAccessInfo
                        {
                            ComponentDisplayName = prior.ComponentDisplayName,
                        };
                        UnionInto(copy.ReadBySystems, prior.ReadBySystems);
                        UnionInto(copy.WrittenBySystems, prior.WrittenBySystems);
                        fresh.Access.Add(copy);
                        byComponent[prior.ComponentDisplayName] = copy;
                        accessChanged = true;
                    }
                }
            }
            if (accessChanged)
            {
                foreach (var entry in fresh.Access)
                {
                    entry.ReadBySystems.Sort(StringComparer.OrdinalIgnoreCase);
                    entry.WrittenBySystems.Sort(StringComparer.OrdinalIgnoreCase);
                }
                fresh.Access.Sort(
                    (a, b) =>
                        string.Compare(
                            a.ComponentDisplayName ?? string.Empty,
                            b.ComponentDisplayName ?? string.Empty,
                            StringComparison.OrdinalIgnoreCase
                        )
                );
            }

            bool tagsChanged = false;
            if (existing.TagsTouched != null && existing.TagsTouched.Count > 0)
            {
                var byAccessor = new Dictionary<string, TrecsSchemaTagsTouchedInfo>(
                    fresh.TagsTouched.Count
                );
                foreach (var entry in fresh.TagsTouched)
                {
                    if (entry.AccessorDebugName != null)
                    {
                        byAccessor[entry.AccessorDebugName] = entry;
                    }
                }
                foreach (var prior in existing.TagsTouched)
                {
                    if (prior?.AccessorDebugName == null)
                    {
                        continue;
                    }
                    if (byAccessor.TryGetValue(prior.AccessorDebugName, out var current))
                    {
                        tagsChanged |= UnionInto(current.TagNames, prior.TagNames);
                    }
                    else
                    {
                        var copy = new TrecsSchemaTagsTouchedInfo
                        {
                            AccessorDebugName = prior.AccessorDebugName,
                        };
                        UnionInto(copy.TagNames, prior.TagNames);
                        fresh.TagsTouched.Add(copy);
                        byAccessor[prior.AccessorDebugName] = copy;
                        tagsChanged = true;
                    }
                }
            }
            if (tagsChanged)
            {
                foreach (var entry in fresh.TagsTouched)
                {
                    entry.TagNames.Sort(StringComparer.OrdinalIgnoreCase);
                }
                fresh.TagsTouched.Sort(
                    (a, b) =>
                        string.Compare(
                            a.AccessorDebugName ?? string.Empty,
                            b.AccessorDebugName ?? string.Empty,
                            StringComparison.OrdinalIgnoreCase
                        )
                );
            }

            bool structuralChanged = false;
            if (existing.Structural != null && existing.Structural.Count > 0)
            {
                fresh.Structural ??= new List<TrecsSchemaStructuralInfo>();
                var byAccessor = new Dictionary<string, TrecsSchemaStructuralInfo>(
                    fresh.Structural.Count
                );
                foreach (var entry in fresh.Structural)
                {
                    if (entry?.AccessorDebugName != null)
                    {
                        byAccessor[entry.AccessorDebugName] = entry;
                    }
                }
                foreach (var prior in existing.Structural)
                {
                    if (prior?.AccessorDebugName == null)
                    {
                        continue;
                    }
                    if (byAccessor.TryGetValue(prior.AccessorDebugName, out var current))
                    {
                        structuralChanged |= UnionInto(
                            current.AddedTemplateNames,
                            prior.AddedTemplateNames
                        );
                        structuralChanged |= UnionInto(
                            current.RemovedTemplateNames,
                            prior.RemovedTemplateNames
                        );
                        structuralChanged |= UnionInto(
                            current.MovedTemplateNames,
                            prior.MovedTemplateNames
                        );
                    }
                    else
                    {
                        var copy = new TrecsSchemaStructuralInfo
                        {
                            AccessorDebugName = prior.AccessorDebugName,
                        };
                        UnionInto(copy.AddedTemplateNames, prior.AddedTemplateNames);
                        UnionInto(copy.RemovedTemplateNames, prior.RemovedTemplateNames);
                        UnionInto(copy.MovedTemplateNames, prior.MovedTemplateNames);
                        fresh.Structural.Add(copy);
                        byAccessor[prior.AccessorDebugName] = copy;
                        structuralChanged = true;
                    }
                }
            }
            if (structuralChanged)
            {
                foreach (var entry in fresh.Structural)
                {
                    entry.AddedTemplateNames.Sort(StringComparer.OrdinalIgnoreCase);
                    entry.RemovedTemplateNames.Sort(StringComparer.OrdinalIgnoreCase);
                    entry.MovedTemplateNames.Sort(StringComparer.OrdinalIgnoreCase);
                }
                fresh.Structural.Sort(
                    (a, b) =>
                        string.Compare(
                            a.AccessorDebugName ?? string.Empty,
                            b.AccessorDebugName ?? string.Empty,
                            StringComparison.OrdinalIgnoreCase
                        )
                );
            }
        }

        // Returns true if anything was actually added to `sink`. Callers use
        // the flag to skip a list re-sort when the merge was a no-op.
        static bool UnionInto(List<string> sink, List<string> additional)
        {
            if (additional == null || additional.Count == 0)
            {
                return false;
            }
            bool added = false;
            // Small lists (handfuls of names) — linear Contains beats a HashSet.
            foreach (var name in additional)
            {
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }
                if (!sink.Contains(name))
                {
                    sink.Add(name);
                    added = true;
                }
            }
            return added;
        }

        static TrecsSchema Build(World world)
        {
            var schema = new TrecsSchema
            {
                WorldName = world.DebugName ?? "(unnamed)",
                SavedAtIso = DateTime.UtcNow.ToString("o"),
            };

            var info = world.WorldInfo;

            foreach (var t in info.AllTemplates)
            {
                var st = new TrecsSchemaTemplate
                {
                    DebugName = t.DebugName ?? "(unnamed)",
                    IsResolved = info.IsResolvedTemplate(t),
                };
                if (st.IsResolved)
                {
                    foreach (var rt in info.ResolvedTemplates)
                    {
                        if (rt.Template != t)
                        {
                            continue;
                        }
                        if (!rt.AllTags.IsNull)
                        {
                            foreach (var tag in rt.AllTags.Tags)
                            {
                                st.AllTagNames.Add(tag.ToString());
                            }
                        }
                        foreach (var b in rt.AllBaseTemplates)
                        {
                            st.BaseTemplateNames.Add(b.DebugName ?? "(unnamed)");
                        }
                        var directTypes = new HashSet<Type>();
                        foreach (var ld in t.LocalComponentDeclarations)
                        {
                            if (ld.ComponentType != null)
                            {
                                directTypes.Add(ld.ComponentType);
                            }
                        }
                        foreach (var d in rt.ComponentDeclarations)
                        {
                            if (d.ComponentType == null)
                                continue;
                            var name = TrecsHierarchyWindow.ComponentTypeDisplayName(
                                d.ComponentType
                            );
                            st.ComponentTypeNames.Add(name);
                            if (directTypes.Contains(d.ComponentType))
                            {
                                st.DirectComponentTypeNames.Add(name);
                            }
                            else
                            {
                                st.InheritedComponentTypeNames.Add(name);
                            }
                        }
                        st.DirectComponentTypeNames.Sort(StringComparer.OrdinalIgnoreCase);
                        st.InheritedComponentTypeNames.Sort(StringComparer.OrdinalIgnoreCase);
                        foreach (var p in rt.Partitions)
                        {
                            var sp = new TrecsSchemaPartition();
                            if (!p.IsNull)
                            {
                                foreach (var tag in p.Tags)
                                {
                                    sp.TagNames.Add(tag.ToString());
                                }
                            }
                            st.Partitions.Add(sp);
                        }
                        break;
                    }
                }
                schema.Templates.Add(st);
            }

            // Component types — union across resolved templates.
            var seen = new HashSet<Type>();
            foreach (var rt in info.ResolvedTemplates)
            {
                foreach (var d in rt.ComponentDeclarations)
                {
                    if (d.ComponentType != null && seen.Add(d.ComponentType))
                    {
                        var ct = new TrecsSchemaComponentType
                        {
                            DisplayName = TrecsHierarchyWindow.ComponentTypeDisplayName(
                                d.ComponentType
                            ),
                            FullName = d.ComponentType.FullName ?? d.ComponentType.Name,
                        };
                        PopulateFields(ct.Fields, d.ComponentType);
                        schema.ComponentTypes.Add(ct);
                    }
                }
            }

            // Derived templates — invert BaseTemplateNames once, write
            // into each TrecsSchemaTemplate so cache mode doesn't have to
            // walk every template per inspector tick.
            PopulateDerivedTemplates(schema.Templates);

            // Systems and manual accessors. We need a WorldAccessor to read
            // the system list — pull it via the same trick the inspectors
            // use so we don't leak (cached in TrecsSchemaCacheInternal below).
            var accessor = GetCachedAccessor(world);
            if (accessor != null)
            {
                try
                {
                    var systems = accessor.GetSystems();
                    var debugNameByIndex = new string[systems.Count];
                    for (int i = 0; i < systems.Count; i++)
                    {
                        debugNameByIndex[i] =
                            systems[i].Metadata.DebugName ?? systems[i].System.GetType().Name;
                    }
                    // Walk in the runner's actual execution order so cache
                    // mode's per-phase grouping ends up in the same order as
                    // the live tree. Without this, schema.Systems followed
                    // accessor.GetSystems() registration order, so cache mode
                    // showed each phase's accessors in declaration order
                    // rather than topologically sorted run order.
                    var orderedIndices = new List<int>(systems.Count);
                    var written = new bool[systems.Count];
                    void AppendPhase(IReadOnlyList<int> sorted)
                    {
                        if (sorted == null)
                        {
                            return;
                        }
                        foreach (var idx in sorted)
                        {
                            if (idx >= 0 && idx < systems.Count && !written[idx])
                            {
                                written[idx] = true;
                                orderedIndices.Add(idx);
                            }
                        }
                    }
                    AppendPhase(accessor.GetSortedEarlyPresentationSystems());
                    AppendPhase(accessor.GetSortedInputSystems());
                    AppendPhase(accessor.GetSortedFixedSystems());
                    AppendPhase(accessor.GetSortedPresentationSystems());
                    AppendPhase(accessor.GetSortedLatePresentationSystems());
                    for (int i = 0; i < systems.Count; i++)
                    {
                        if (!written[i])
                        {
                            orderedIndices.Add(i);
                        }
                    }
                    foreach (var sysIdx in orderedIndices)
                    {
                        var s = systems[sysIdx];
                        var sysType = s.System.GetType();
                        var sysSchema = new TrecsSchemaSystem
                        {
                            DebugName = s.Metadata.DebugName ?? sysType.Name,
                            TypeName = sysType.Name,
                            TypeNamespace = sysType.Namespace ?? string.Empty,
                            Phase = s.Metadata.Phase.ToString(),
                            // System-owned accessor's role mirrors phase.
                            Role = s.Metadata.Phase.ToAccessorRole().ToString(),
                            HasPriority = s.Metadata.ExecutionPriority.HasValue,
                            Priority = s.Metadata.ExecutionPriority ?? 0,
                        };
                        var deps = s.Metadata.SystemDependencies;
                        if (deps != null)
                        {
                            foreach (var dep in deps)
                            {
                                if (dep >= 0 && dep < debugNameByIndex.Length)
                                {
                                    sysSchema.DependsOnSystemDebugNames.Add(debugNameByIndex[dep]);
                                }
                            }
                        }
                        schema.Systems.Add(sysSchema);
                    }
                    // Dependents — invert DependsOnSystemDebugNames once.
                    PopulateDependents(schema.Systems);
                }
                catch (Exception)
                {
                    // System list not available yet — skip.
                }

                // Manual accessors — every accessor whose DebugName is not in
                // a system metadata entry, with our editor-owned ones filtered.
                try
                {
                    var systemAccessorIds = new HashSet<int>();
                    foreach (var s in accessor.GetSystems())
                    {
                        if (s.Metadata.Accessor != null)
                        {
                            systemAccessorIds.Add(s.Metadata.Accessor.Id);
                        }
                    }
                    foreach (var entry in world.GetAccessorsById())
                    {
                        var a = entry.Value;
                        if (a == null || systemAccessorIds.Contains(entry.Key))
                        {
                            continue;
                        }
                        if (TrecsEditorAccessorNames.Contains(a.DebugName))
                        {
                            continue;
                        }
                        schema.ManualAccessors.Add(
                            new TrecsSchemaAccessor
                            {
                                DebugName = a.DebugName ?? $"#{entry.Key}",
                                Role = a.Role.ToString(),
                                CreatedAtFile = a.CreatedAtFile ?? string.Empty,
                                CreatedAtLine = a.CreatedAtLine,
                            }
                        );
                    }
                }
                catch (Exception)
                {
                    // Skip on failure.
                }
            }

            foreach (var entitySet in info.AllSets)
            {
                var ss = new TrecsSchemaSet
                {
                    DebugName = entitySet.DebugName ?? $"#{entitySet.Id.Id}",
                    Id = entitySet.Id.Id,
                    TypeFullName = entitySet.SetType?.FullName,
                    TypeNamespace = entitySet.SetType?.Namespace,
                };
                if (!entitySet.Tags.IsNull)
                {
                    foreach (var tag in entitySet.Tags.Tags)
                    {
                        ss.TagNames.Add(tag.ToString());
                    }
                }
                schema.Sets.Add(ss);
            }

            // Tags: one entry per unique tag (by Guid) referenced anywhere
            // in templates' AllTags / partitions or sets' Tags. Per-tag
            // Templates/Sets lists let cache mode render the tag inspector
            // without re-walking everything.
            var tagsByGuid = new Dictionary<int, TrecsSchemaTag>();
            foreach (var rt in info.ResolvedTemplates)
            {
                AccumulateTagsFromTagSet(tagsByGuid, rt.AllTags);
                foreach (var p in rt.Partitions)
                {
                    AccumulateTagsFromTagSet(tagsByGuid, p);
                }
            }
            foreach (var entitySet in info.AllSets)
            {
                AccumulateTagsFromTagSet(tagsByGuid, entitySet.Tags);
            }
            // Cross-link templates and sets back to tag entries.
            foreach (var rt in info.ResolvedTemplates)
            {
                var tplName = rt.DebugName ?? "(unnamed)";
                AddTagSetCrossLink(tagsByGuid, rt.AllTags, t => t.TemplateNames.Add(tplName));
                foreach (var p in rt.Partitions)
                {
                    AddTagSetCrossLink(
                        tagsByGuid,
                        p,
                        t =>
                        {
                            if (!t.TemplateNames.Contains(tplName))
                            {
                                t.TemplateNames.Add(tplName);
                            }
                        }
                    );
                }
            }
            foreach (var entitySet in info.AllSets)
            {
                var setName = entitySet.DebugName ?? $"#{entitySet.Id.Id}";
                AddTagSetCrossLink(tagsByGuid, entitySet.Tags, t => t.SetNames.Add(setName));
            }
            foreach (var entry in tagsByGuid.Values)
            {
                schema.Tags.Add(entry);
            }

            // Access data — one entry per component type that's been
            // touched. We walk the seen component types and ask the tracker
            // for readers/writers; types with neither are skipped to keep
            // the JSON small.
            var tracker = TrecsAccessRegistry.GetTracker(world);
            if (tracker != null)
            {
                foreach (var t in seen)
                {
                    ComponentId id;
                    try
                    {
                        id = new ComponentId(TypeIdProvider.GetTypeId(t));
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    var readers = tracker.GetReadersOf(id);
                    var writers = tracker.GetWritersOf(id);
                    if ((readers?.Count ?? 0) == 0 && (writers?.Count ?? 0) == 0)
                    {
                        continue;
                    }
                    var info_ = new TrecsSchemaAccessInfo
                    {
                        ComponentDisplayName = TrecsHierarchyWindow.ComponentTypeDisplayName(t),
                    };
                    if (readers != null)
                    {
                        foreach (var n in readers)
                        {
                            info_.ReadBySystems.Add(n);
                        }
                        info_.ReadBySystems.Sort(StringComparer.OrdinalIgnoreCase);
                    }
                    if (writers != null)
                    {
                        foreach (var n in writers)
                        {
                            info_.WrittenBySystems.Add(n);
                        }
                        info_.WrittenBySystems.Sort(StringComparer.OrdinalIgnoreCase);
                    }
                    schema.Access.Add(info_);
                }
                schema.Access.Sort(
                    (a, b) =>
                        string.Compare(
                            a.ComponentDisplayName ?? string.Empty,
                            b.ComponentDisplayName ?? string.Empty,
                            StringComparison.OrdinalIgnoreCase
                        )
                );

                // Tags-touched per accessor — same source data the live
                // accessor inspector derives at render time, captured here
                // so cache mode doesn't have to fall back to "(runtime
                // tracker data not available)". Iterate accessors known to
                // the tracker, resolve each touched group's tags via the
                // live WorldInfo, and persist the dedup'd union per accessor.
                foreach (var accessorName in tracker.GetAllTrackedAccessorNames())
                {
                    var groups = tracker.GetGroupsTouchedBy(accessorName);
                    if (groups == null || groups.Count == 0)
                    {
                        continue;
                    }
                    var seenTagNames = new HashSet<string>();
                    var tagNames = new List<string>();
                    foreach (var g in groups)
                    {
                        foreach (var tag in info.GetGroupTags(g))
                        {
                            var name = tag.ToString();
                            if (!string.IsNullOrEmpty(name) && seenTagNames.Add(name))
                            {
                                tagNames.Add(name);
                            }
                        }
                    }
                    if (tagNames.Count == 0)
                    {
                        continue;
                    }
                    tagNames.Sort(StringComparer.OrdinalIgnoreCase);
                    schema.TagsTouched.Add(
                        new TrecsSchemaTagsTouchedInfo
                        {
                            AccessorDebugName = accessorName,
                            TagNames = tagNames,
                        }
                    );
                }
                schema.TagsTouched.Sort(
                    (a, b) =>
                        string.Compare(
                            a.AccessorDebugName ?? string.Empty,
                            b.AccessorDebugName ?? string.Empty,
                            StringComparison.OrdinalIgnoreCase
                        )
                );

                // Per-accessor structural changes (Add / Remove / Move),
                // recorded against template DebugNames so cache mode answers
                // the same template-level questions as live mode.
                foreach (var accessorName in tracker.GetAllTrackedAccessorNames())
                {
                    var added = ProjectGroupsToTemplateNames(
                        info,
                        tracker.GetGroupsAddedBy(accessorName)
                    );
                    var removed = ProjectGroupsToTemplateNames(
                        info,
                        tracker.GetGroupsRemovedBy(accessorName)
                    );
                    var moved = ProjectGroupsToTemplateNames(
                        info,
                        tracker.GetGroupsMovedBy(accessorName)
                    );
                    if (added.Count == 0 && removed.Count == 0 && moved.Count == 0)
                    {
                        continue;
                    }
                    schema.Structural.Add(
                        new TrecsSchemaStructuralInfo
                        {
                            AccessorDebugName = accessorName,
                            AddedTemplateNames = added,
                            RemovedTemplateNames = removed,
                            MovedTemplateNames = moved,
                        }
                    );
                }
                schema.Structural.Sort(
                    (a, b) =>
                        string.Compare(
                            a.AccessorDebugName ?? string.Empty,
                            b.AccessorDebugName ?? string.Empty,
                            StringComparison.OrdinalIgnoreCase
                        )
                );
            }

            return schema;
        }

        static List<string> ProjectGroupsToTemplateNames(
            WorldInfo info,
            IReadOnlyCollection<GroupIndex> groups
        )
        {
            if (groups == null || groups.Count == 0)
            {
                return new List<string>();
            }
            var seen = new HashSet<string>();
            var names = new List<string>();
            foreach (var g in groups)
            {
                var template = info.GetResolvedTemplateForGroup(g);
                var name = template?.DebugName;
                if (string.IsNullOrEmpty(name) || !seen.Add(name))
                {
                    continue;
                }
                names.Add(name);
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        // Walk the component struct's instance fields (public + non-public)
        // and capture (name, type) pairs for the inspector to render.
        // Components are unmanaged structs by contract, so reflection is
        // safe and cheap.
        public static void PopulateFields(List<TrecsSchemaField> sink, Type componentType)
        {
            try
            {
                var fields = componentType.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                );
                foreach (var f in fields)
                {
                    sink.Add(
                        new TrecsSchemaField
                        {
                            Name = f.Name,
                            TypeName = FormatFieldTypeName(f.FieldType),
                        }
                    );
                }
            }
            catch (Exception)
            {
                // Reflection unavailable on this type — leave Fields empty.
            }
        }

        // Inverse of BaseTemplateNames. Each template's
        // DerivedTemplateNames is filled with every other template that
        // lists it as a base.
        static void PopulateDerivedTemplates(List<TrecsSchemaTemplate> templates)
        {
            var byName = new Dictionary<string, TrecsSchemaTemplate>(templates.Count);
            foreach (var t in templates)
            {
                if (!string.IsNullOrEmpty(t.DebugName))
                {
                    byName[t.DebugName] = t;
                }
            }
            foreach (var t in templates)
            {
                if (t.BaseTemplateNames == null)
                    continue;
                foreach (var baseName in t.BaseTemplateNames)
                {
                    if (byName.TryGetValue(baseName, out var baseEntry))
                    {
                        baseEntry.DerivedTemplateNames.Add(t.DebugName);
                    }
                }
            }
            foreach (var t in templates)
            {
                t.DerivedTemplateNames.Sort(StringComparer.OrdinalIgnoreCase);
            }
        }

        // Inverse of DependsOnSystemDebugNames. Each system's
        // DependentSystemDebugNames is filled with every other system that
        // lists it as a dep.
        static void PopulateDependents(List<TrecsSchemaSystem> systems)
        {
            var byName = new Dictionary<string, TrecsSchemaSystem>(systems.Count);
            foreach (var s in systems)
            {
                if (!string.IsNullOrEmpty(s.DebugName))
                {
                    byName[s.DebugName] = s;
                }
            }
            foreach (var s in systems)
            {
                if (s.DependsOnSystemDebugNames == null)
                    continue;
                foreach (var depName in s.DependsOnSystemDebugNames)
                {
                    if (byName.TryGetValue(depName, out var depEntry))
                    {
                        depEntry.DependentSystemDebugNames.Add(s.DebugName);
                    }
                }
            }
            foreach (var s in systems)
            {
                s.DependentSystemDebugNames.Sort(StringComparer.OrdinalIgnoreCase);
            }
        }

        // Pretty-prints generic types (List<int> rather than List`1) and
        // strips common prefixes for readability in the inspector.
        public static string FormatFieldTypeName(Type t)
        {
            if (t == null)
                return "?";
            if (!t.IsGenericType)
            {
                return t.Name;
            }
            var name = t.Name;
            var backtick = name.IndexOf('`');
            if (backtick >= 0)
            {
                name = name.Substring(0, backtick);
            }
            var args = t.GetGenericArguments();
            var sb = new StringBuilder();
            sb.Append(name);
            sb.Append('<');
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0)
                    sb.Append(", ");
                sb.Append(FormatFieldTypeName(args[i]));
            }
            sb.Append('>');
            return sb.ToString();
        }

        static void AccumulateTagsFromTagSet(Dictionary<int, TrecsSchemaTag> map, TagSet ts)
        {
            if (ts.IsNull)
            {
                return;
            }
            foreach (var tag in ts.Tags)
            {
                if (tag.Guid == 0 || map.ContainsKey(tag.Guid))
                {
                    continue;
                }
                map[tag.Guid] = new TrecsSchemaTag
                {
                    Name = tag.ToString() ?? $"#{tag.Guid}",
                    Guid = tag.Guid,
                };
            }
        }

        static void AddTagSetCrossLink(
            Dictionary<int, TrecsSchemaTag> map,
            TagSet ts,
            Action<TrecsSchemaTag> onMatch
        )
        {
            if (ts.IsNull)
            {
                return;
            }
            foreach (var tag in ts.Tags)
            {
                if (tag.Guid != 0 && map.TryGetValue(tag.Guid, out var entry))
                {
                    onMatch(entry);
                }
            }
        }

        // Per-world accessor so we don't allocate a fresh one on every
        // schema save and pollute the world's accessor count (which the
        // hierarchy uses as part of its structural fingerprint).
        static readonly Dictionary<World, WorldAccessor> _cachedAccessors = new();

        static WorldAccessor GetCachedAccessor(World world)
        {
            if (_cachedAccessors.TryGetValue(world, out var a) && !world.IsDisposed)
            {
                return a;
            }
            try
            {
                a = world.CreateAccessor(AccessorRole.Unrestricted, "TrecsSchemaCache");
                _cachedAccessors[world] = a;
                return a;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
