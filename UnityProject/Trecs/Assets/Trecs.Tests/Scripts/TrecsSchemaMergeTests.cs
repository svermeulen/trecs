using System;
using System.Collections.Generic;
using NUnit.Framework;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class TrecsSchemaMergeTests
    {
        [Test]
        public void Merge_OverlappingAccess_UnionsReadersAndWriters()
        {
            var existing = SchemaWithAccess(
                ("CHealth", reads: new[] { "FooSystem" }, writes: new[] { "BarSystem" })
            );
            var fresh = SchemaWithAccess(
                ("CHealth", reads: new[] { "BazSystem" }, writes: new[] { "BarSystem" })
            );

            TrecsSchemaCache.MergeRuntimeData(existing, fresh);

            NAssert.AreEqual(1, fresh.Access.Count);
            CollectionAssert.AreEqual(
                new[] { "BazSystem", "FooSystem" },
                fresh.Access[0].ReadBySystems
            );
            CollectionAssert.AreEqual(new[] { "BarSystem" }, fresh.Access[0].WrittenBySystems);
        }

        [Test]
        public void Merge_PriorOnlyEntry_CopiedIntoFresh()
        {
            var existing = SchemaWithAccess(
                ("CHealth", reads: new[] { "FooSystem" }, writes: Array.Empty<string>())
            );
            var fresh = SchemaWithAccess();

            TrecsSchemaCache.MergeRuntimeData(existing, fresh);

            NAssert.AreEqual(1, fresh.Access.Count);
            NAssert.AreEqual("CHealth", fresh.Access[0].ComponentDisplayName);
            CollectionAssert.AreEqual(new[] { "FooSystem" }, fresh.Access[0].ReadBySystems);
        }

        [Test]
        public void Merge_FreshOnlyEntry_LeftIntact()
        {
            var existing = SchemaWithAccess();
            var fresh = SchemaWithAccess(
                ("CPosition", reads: new[] { "MoveSystem" }, writes: Array.Empty<string>())
            );

            TrecsSchemaCache.MergeRuntimeData(existing, fresh);

            NAssert.AreEqual(1, fresh.Access.Count);
            CollectionAssert.AreEqual(new[] { "MoveSystem" }, fresh.Access[0].ReadBySystems);
        }

        [Test]
        public void Merge_ResultIsSortedByComponent_AndPerEntryListsAreSorted()
        {
            var existing = SchemaWithAccess(
                ("ZComp", reads: new[] { "ZSys" }, writes: Array.Empty<string>()),
                ("AComp", reads: new[] { "ASys", "ZSys" }, writes: Array.Empty<string>())
            );
            var fresh = SchemaWithAccess(
                ("MComp", reads: new[] { "MSys" }, writes: Array.Empty<string>()),
                ("AComp", reads: new[] { "BSys" }, writes: Array.Empty<string>())
            );

            TrecsSchemaCache.MergeRuntimeData(existing, fresh);

            // Outer list sorted by component name.
            CollectionAssert.AreEqual(
                new[] { "AComp", "MComp", "ZComp" },
                ProjectComponents(fresh.Access)
            );
            // Per-entry readers sorted ordinal-ignore-case.
            var aComp = fresh.Access.Find(e => e.ComponentDisplayName == "AComp");
            CollectionAssert.AreEqual(new[] { "ASys", "BSys", "ZSys" }, aComp.ReadBySystems);
        }

        [Test]
        public void Merge_DuplicateNames_DedupedNotDuplicated()
        {
            var existing = SchemaWithAccess(
                ("CHealth", reads: new[] { "FooSystem" }, writes: Array.Empty<string>())
            );
            var fresh = SchemaWithAccess(
                ("CHealth", reads: new[] { "FooSystem" }, writes: Array.Empty<string>())
            );

            TrecsSchemaCache.MergeRuntimeData(existing, fresh);

            NAssert.AreEqual(1, fresh.Access.Count);
            CollectionAssert.AreEqual(new[] { "FooSystem" }, fresh.Access[0].ReadBySystems);
        }

        [Test]
        public void Merge_TagsTouched_UnionsAcrossSessionsAndSorts()
        {
            var existing = new TrecsSchema();
            existing.TagsTouched.Add(MakeTags("MoveSystem", "Player", "Mob"));
            existing.TagsTouched.Add(MakeTags("AISystem", "Mob"));

            var fresh = new TrecsSchema();
            fresh.TagsTouched.Add(MakeTags("MoveSystem", "Boss"));

            TrecsSchemaCache.MergeRuntimeData(existing, fresh);

            // Outer sorted by accessor name; AISystem copied over from prior.
            CollectionAssert.AreEqual(
                new[] { "AISystem", "MoveSystem" },
                ProjectAccessors(fresh.TagsTouched)
            );
            var move = fresh.TagsTouched.Find(e => e.AccessorDebugName == "MoveSystem");
            CollectionAssert.AreEqual(new[] { "Boss", "Mob", "Player" }, move.TagNames);
            var ai = fresh.TagsTouched.Find(e => e.AccessorDebugName == "AISystem");
            CollectionAssert.AreEqual(new[] { "Mob" }, ai.TagNames);
        }

        [Test]
        public void Merge_StaticFieldsLeftUntouched()
        {
            var existing = new TrecsSchema { WorldName = "Stale" };
            existing.Templates.Add(new TrecsSchemaTemplate { DebugName = "OldOnly" });
            existing.Systems.Add(new TrecsSchemaSystem { DebugName = "OldSys" });

            var fresh = new TrecsSchema { WorldName = "Current" };
            fresh.Templates.Add(new TrecsSchemaTemplate { DebugName = "NewOnly" });
            fresh.Systems.Add(new TrecsSchemaSystem { DebugName = "NewSys" });

            TrecsSchemaCache.MergeRuntimeData(existing, fresh);

            // Fresh wins for static fields — prior templates/systems
            // mustn't bleed into the merged result.
            NAssert.AreEqual("Current", fresh.WorldName);
            CollectionAssert.AreEqual(new[] { "NewOnly" }, ProjectTemplateNames(fresh.Templates));
            CollectionAssert.AreEqual(new[] { "NewSys" }, ProjectSystemNames(fresh.Systems));
        }

        [Test]
        public void Merge_BothEmpty_LeavesFreshEmpty()
        {
            var existing = new TrecsSchema();
            var fresh = new TrecsSchema();

            TrecsSchemaCache.MergeRuntimeData(existing, fresh);

            NAssert.AreEqual(0, fresh.Access.Count);
            NAssert.AreEqual(0, fresh.TagsTouched.Count);
        }

        // ── helpers ────────────────────────────────────────────────────

        static TrecsSchema SchemaWithAccess(
            params (string component, string[] reads, string[] writes)[] entries
        )
        {
            var s = new TrecsSchema();
            foreach (var e in entries)
            {
                var info = new TrecsSchemaAccessInfo { ComponentDisplayName = e.component };
                info.ReadBySystems.AddRange(e.reads);
                info.WrittenBySystems.AddRange(e.writes);
                s.Access.Add(info);
            }
            return s;
        }

        static TrecsSchemaTagsTouchedInfo MakeTags(string accessor, params string[] tags)
        {
            var info = new TrecsSchemaTagsTouchedInfo { AccessorDebugName = accessor };
            info.TagNames.AddRange(tags);
            return info;
        }

        static List<string> ProjectComponents(List<TrecsSchemaAccessInfo> entries)
        {
            var r = new List<string>(entries.Count);
            foreach (var e in entries)
                r.Add(e.ComponentDisplayName);
            return r;
        }

        static List<string> ProjectAccessors(List<TrecsSchemaTagsTouchedInfo> entries)
        {
            var r = new List<string>(entries.Count);
            foreach (var e in entries)
                r.Add(e.AccessorDebugName);
            return r;
        }

        static List<string> ProjectTemplateNames(List<TrecsSchemaTemplate> entries)
        {
            var r = new List<string>(entries.Count);
            foreach (var e in entries)
                r.Add(e.DebugName);
            return r;
        }

        static List<string> ProjectSystemNames(List<TrecsSchemaSystem> entries)
        {
            var r = new List<string>(entries.Count);
            foreach (var e in entries)
                r.Add(e.DebugName);
            return r;
        }
    }
}
