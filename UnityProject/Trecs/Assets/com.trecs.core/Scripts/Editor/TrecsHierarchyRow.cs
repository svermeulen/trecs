using System;
using UnityEditor;
using UnityEngine;

namespace Trecs
{
    enum RowKind
    {
        Section,
        Template,
        AbstractTemplate,
        Group,
        Entity,
        MorePlaceholder,
        AccessorPhase,
        Accessor,
        ComponentType,
        SetItem,
        TagItem,
    }

    // Single mutable row payload shared across all kinds. Each TreeView
    // item carries a reference to one of these, so periodic refresh can
    // mutate fields (Count, SystemEnabled) and a follow-up RefreshItems
    // re-binds visible rows without touching tree structure.
    sealed class RowData
    {
        public RowKind Kind;
        public string DisplayName;

        // Mode-agnostic identity (e.g. "template:Foo#1", "accessor:MoveSystem")
        // stamped at row build time. Doubles as the proxy identity for
        // identity-based selection proxies and as the persisted SessionState
        // identity for cross-rebuild selection restoration.
        public string StableKey;
        public ResolvedTemplate ResolvedTemplate;
        public Template Template;
        public GroupIndex Group;
        public TagSet PartitionTags;
        public EntityHandle EntityHandle;
        public int AccessorId;
        public int SystemIndex;
        public int? ExecutionPriority;
        public bool SystemEnabled;
        public Type ComponentType;
        public EntitySet EntitySet;
        public Tag Tag;
        public int Count;
        public bool ShowCount;
    }

    struct AccessorRowSpec
    {
        public int AccessorId;
        public string DisplayName;
        public int SystemIndex; // -1 for manual accessors
        public int? ExecutionPriority;
        public bool SystemEnabled;
    }

    // Icons for hierarchy tree rows. Lazy-cached on first access; not
    // invalidated on theme switch (icons stay until domain reload).
    static class TrecsRowIcons
    {
        static Texture _template;
        static Texture _entity;
        static Texture _folder;
        static Texture _script;
        static Texture _scriptable;

        public static Texture For(RowKind kind)
        {
            switch (kind)
            {
                case RowKind.Template:
                case RowKind.AbstractTemplate:
                    return _template ??= EditorGUIUtility.IconContent("Prefab Icon").image;
                case RowKind.Entity:
                    return _entity ??= EditorGUIUtility.IconContent("GameObject Icon").image;
                case RowKind.Group:
                case RowKind.AccessorPhase:
                case RowKind.SetItem:
                    return _folder ??= EditorGUIUtility.IconContent("Folder Icon").image;
                case RowKind.Accessor:
                    return _script ??= EditorGUIUtility.IconContent("cs Script Icon").image;
                case RowKind.ComponentType:
                    return _scriptable ??= EditorGUIUtility
                        .IconContent("ScriptableObject Icon")
                        .image;
                case RowKind.TagItem:
                    return EditorGUIUtility.IconContent("FilterByLabel").image;
                default:
                    return null;
            }
        }
    }
}
