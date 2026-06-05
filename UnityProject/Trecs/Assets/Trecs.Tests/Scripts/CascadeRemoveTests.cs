using NUnit.Framework;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    // ── Tags ──────────────────────────────────────────────────────────────
    public struct CascadeOwnerTag : ITag { }

    public struct CascadeChildTag : ITag { }

    public struct CascadeLinkTag : ITag { }

    public struct CascadeTargetTag : ITag { }

    public struct DisposeOnRemoveTag : ITag { }

    public static class CascadeTags
    {
        public static readonly Tag Owner = Tag<CascadeOwnerTag>.Value;
        public static readonly Tag Child = Tag<CascadeChildTag>.Value;
        public static readonly Tag Link = Tag<CascadeLinkTag>.Value;
        public static readonly Tag Target = Tag<CascadeTargetTag>.Value;
        public static readonly Tag DisposeOnRemove = Tag<DisposeOnRemoveTag>.Value;
    }

    // ── Components ────────────────────────────────────────────────────────

    // Owns a list of children: removing the owner removes the children
    // (cascade) AND frees the list (auto-dispose) — the composition case.
    public partial struct CascadeOwner : IEntityComponent
    {
        [CascadeRemove, DisposeOnRemove]
        public TrecsList<EntityHandle> Children;
    }

    public partial struct CascadeChildMarker : IEntityComponent
    {
        public int Value;
    }

    // Single forward reference removed on owner removal.
    public partial struct CascadeLink : IEntityComponent
    {
        [CascadeRemove]
        public EntityHandle Target;
    }

    // Heap-backed field auto-disposed (no entity references involved).
    public partial struct DisposeOnRemoveHolder : IEntityComponent
    {
        [DisposeOnRemove]
        public TrecsList<int> Data;
    }

    public static class CascadeTemplates
    {
        public static Template Owner =>
            TestTemplate
                .Named("CascadeOwner")
                .WithTags(CascadeTags.Owner)
                .WithComponent<CascadeOwner>(default(CascadeOwner));

        public static Template Child =>
            TestTemplate
                .Named("CascadeChild")
                .WithTags(CascadeTags.Child)
                .WithComponent<CascadeChildMarker>(default(CascadeChildMarker));

        public static Template Link =>
            TestTemplate
                .Named("CascadeLink")
                .WithTags(CascadeTags.Link)
                .WithComponent<CascadeLink>(default(CascadeLink));

        public static Template Target =>
            TestTemplate
                .Named("CascadeTarget")
                .WithTags(CascadeTags.Target)
                .WithComponent<CascadeChildMarker>(default(CascadeChildMarker));

        public static Template Holder =>
            TestTemplate
                .Named("CascadeHolder")
                .WithTags(CascadeTags.DisposeOnRemove)
                .WithComponent<DisposeOnRemoveHolder>(default(DisposeOnRemoveHolder));
    }

    [TestFixture]
    public class CascadeRemoveTests
    {
        static EntityHandle AddOwnerWithChildren(WorldAccessor a, params EntityHandle[] children)
        {
            var list = TrecsList.Alloc<EntityHandle>(a, children.Length);
            var w = list.Write(a);
            foreach (var c in children)
            {
                w.Add(c);
            }
            return a.AddEntity(CascadeTags.Owner)
                .Set(new CascadeOwner { Children = list })
                .AssertComplete()
                .Handle;
        }

        [Test]
        public void CascadeRemove_HandleList_RemovesChildren()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                CascadeTemplates.Owner,
                CascadeTemplates.Child
            );
            var a = env.Accessor;

            var c1 = a.AddEntity(CascadeTags.Child).AssertComplete().Handle;
            var c2 = a.AddEntity(CascadeTags.Child).AssertComplete().Handle;
            var owner = AddOwnerWithChildren(a, c1, c2);
            a.World.Submit();

            NAssert.AreEqual(2, a.CountEntitiesWithTags(CascadeTags.Child));

            owner.Remove(a);
            a.World.Submit();

            NAssert.IsFalse(owner.Exists(a));
            NAssert.AreEqual(0, a.CountEntitiesWithTags(CascadeTags.Child));
        }

        [Test]
        public void CascadeRemove_SingleHandle_RemovesTarget()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                CascadeTemplates.Link,
                CascadeTemplates.Target
            );
            var a = env.Accessor;

            var target = a.AddEntity(CascadeTags.Target).AssertComplete().Handle;
            var link = a.AddEntity(CascadeTags.Link)
                .Set(new CascadeLink { Target = target })
                .AssertComplete()
                .Handle;
            a.World.Submit();

            NAssert.AreEqual(1, a.CountEntitiesWithTags(CascadeTags.Target));

            link.Remove(a);
            a.World.Submit();

            NAssert.IsFalse(target.Exists(a));
            NAssert.AreEqual(0, a.CountEntitiesWithTags(CascadeTags.Target));
        }

        [Test]
        public void CascadeRemove_Nested_TearsDownWholeSubtreeInOneSubmit()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                CascadeTemplates.Owner,
                CascadeTemplates.Child
            );
            var a = env.Accessor;

            // grandparent -> parent (owner) -> leaf (child)
            var leaf = a.AddEntity(CascadeTags.Child).AssertComplete().Handle;
            var parent = AddOwnerWithChildren(a, leaf);
            var grandparent = AddOwnerWithChildren(a, parent);
            a.World.Submit();

            NAssert.AreEqual(2, a.CountEntitiesWithTags(CascadeTags.Owner));
            NAssert.AreEqual(1, a.CountEntitiesWithTags(CascadeTags.Child));

            grandparent.Remove(a);
            a.World.Submit();

            NAssert.AreEqual(0, a.CountEntitiesWithTags(CascadeTags.Owner));
            NAssert.AreEqual(0, a.CountEntitiesWithTags(CascadeTags.Child));
        }

        [Test]
        public void CascadeRemove_DeadHandleInList_SkippedCleanly()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                CascadeTemplates.Owner,
                CascadeTemplates.Child
            );
            var a = env.Accessor;

            var c1 = a.AddEntity(CascadeTags.Child).AssertComplete().Handle;
            var c2 = a.AddEntity(CascadeTags.Child).AssertComplete().Handle;
            var owner = AddOwnerWithChildren(a, c1, c2);
            a.World.Submit();

            // Kill one child directly, leaving a stale handle in the owner's list.
            c1.Remove(a);
            a.World.Submit();
            NAssert.AreEqual(1, a.CountEntitiesWithTags(CascadeTags.Child));

            // Removing the owner must skip the dead handle and remove the live one.
            owner.Remove(a);
            a.World.Submit();

            NAssert.AreEqual(0, a.CountEntitiesWithTags(CascadeTags.Child));
        }

        [Test]
        public void CascadeRemove_WholeGroupRemoval_CascadesToChildren()
        {
            // Removing the owners as a whole group (RemoveEntitiesWithTags routes
            // through the full-group removal path, distinct from per-entity
            // removal) must still cascade to children in another group.
            using var env = EcsTestHelper.CreateEnvironment(
                CascadeTemplates.Owner,
                CascadeTemplates.Child
            );
            var a = env.Accessor;

            var c1 = a.AddEntity(CascadeTags.Child).AssertComplete().Handle;
            var c2 = a.AddEntity(CascadeTags.Child).AssertComplete().Handle;
            AddOwnerWithChildren(a, c1);
            AddOwnerWithChildren(a, c2);
            a.World.Submit();

            NAssert.AreEqual(2, a.CountEntitiesWithTags(CascadeTags.Owner));
            NAssert.AreEqual(2, a.CountEntitiesWithTags(CascadeTags.Child));

            a.RemoveEntitiesWithTags(CascadeTags.Owner);
            a.World.Submit();

            NAssert.AreEqual(0, a.CountEntitiesWithTags(CascadeTags.Owner));
            NAssert.AreEqual(0, a.CountEntitiesWithTags(CascadeTags.Child));
        }

        [Test]
        public void DisposeOnRemove_FreesHeapBackedField_NoLeak()
        {
            using var env = EcsTestHelper.CreateEnvironment(CascadeTemplates.Holder);
            var a = env.Accessor;

            int baseline = a.NativeUniqueChunkStore.NumLiveAllocations;

            var list = TrecsList.Alloc<int>(a, 4);
            var w = list.Write(a);
            w.Add(1);
            w.Add(2);
            var holder = a.AddEntity(CascadeTags.DisposeOnRemove)
                .Set(new DisposeOnRemoveHolder { Data = list })
                .AssertComplete()
                .Handle;
            a.World.Submit();

            NAssert.Greater(
                a.NativeUniqueChunkStore.NumLiveAllocations,
                baseline,
                "List allocation should be live while the entity exists."
            );

            holder.Remove(a);
            a.World.Submit();

            NAssert.AreEqual(
                baseline,
                a.NativeUniqueChunkStore.NumLiveAllocations,
                "DisposeOnRemove should have freed the list on per-entity removal."
            );
        }

        [Test]
        public void Composition_CascadeAndDisposeOnRemove_ChildrenGoneAndListFreed()
        {
            using var env = EcsTestHelper.CreateEnvironment(
                CascadeTemplates.Owner,
                CascadeTemplates.Child
            );
            var a = env.Accessor;

            int baseline = a.NativeUniqueChunkStore.NumLiveAllocations;

            var c1 = a.AddEntity(CascadeTags.Child).AssertComplete().Handle;
            var c2 = a.AddEntity(CascadeTags.Child).AssertComplete().Handle;
            var owner = AddOwnerWithChildren(a, c1, c2);
            a.World.Submit();

            owner.Remove(a);
            a.World.Submit();

            NAssert.AreEqual(0, a.CountEntitiesWithTags(CascadeTags.Child));
            NAssert.AreEqual(
                baseline,
                a.NativeUniqueChunkStore.NumLiveAllocations,
                "Children removed (cascade) and the owner's list freed (auto-dispose)."
            );
        }

        [Test]
        public void Ordering_UserOnRemoved_ReadsDisposeOnRemoveFieldBeforeItIsFreed()
        {
            using var env = EcsTestHelper.CreateEnvironment(CascadeTemplates.Holder);
            var a = env.Accessor;

            int observedCount = -1;
            using var sub = a
                .Events.EntitiesWithTags<DisposeOnRemoveTag>()
                .OnRemoved(
                    (GroupIndex group, EntityRange range) =>
                    {
                        var buf = a.ComponentBuffer<DisposeOnRemoveHolder>(group).Read;
                        for (int i = range.Start; i < range.End; i++)
                        {
                            // If DisposeOnRemove had already run, this read would see a
                            // freed list. It must still be valid here.
                            observedCount = buf[i].Data.Read(a).Count;
                        }
                    }
                );

            var list = TrecsList.Alloc<int>(a, 4);
            var w = list.Write(a);
            w.Add(10);
            w.Add(20);
            w.Add(30);
            var holder = a.AddEntity(CascadeTags.DisposeOnRemove)
                .Set(new DisposeOnRemoveHolder { Data = list })
                .AssertComplete()
                .Handle;
            a.World.Submit();

            holder.Remove(a);
            a.World.Submit();

            NAssert.AreEqual(
                3,
                observedCount,
                "User OnRemoved must observe the list intact (fires before DisposeOnRemove)."
            );
        }

        [Test]
        public void WorldDispose_WithCascadeAndDisposeOnRemove_NoLeakOrCrash()
        {
            var env = EcsTestHelper.CreateEnvironment(
                CascadeTemplates.Owner,
                CascadeTemplates.Child,
                CascadeTemplates.Holder
            );
            var a = env.Accessor;

            var c1 = a.AddEntity(CascadeTags.Child).AssertComplete().Handle;
            AddOwnerWithChildren(a, c1);

            var list = TrecsList.Alloc<int>(a, 2);
            list.Write(a).Add(7);
            a.AddEntity(CascadeTags.DisposeOnRemove)
                .Set(new DisposeOnRemoveHolder { Data = list })
                .AssertComplete();
            a.World.Submit();

            // Tear down with live entities still holding heap fields — the
            // whole-group removal path must run DisposeOnRemove so nothing leaks.
            NAssert.DoesNotThrow(() => env.Dispose());
        }
    }
}
