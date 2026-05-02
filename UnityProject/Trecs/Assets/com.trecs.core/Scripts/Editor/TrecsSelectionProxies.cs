using UnityEditor;
using UnityEngine;

namespace Trecs
{
    /// <summary>
    /// Pool of editor-session-scoped selection-proxy ScriptableObjects, one
    /// ring per kind (Template / Entity / Component / Accessor / Set / Tag).
    /// Each call to <c>NextXxx()</c> returns a fresh instance the caller can
    /// stamp data onto and assign to <see cref="Selection.activeObject"/>;
    /// when the ring fills, the oldest slot is recycled.
    ///
    /// Fresh-per-click matters because external selection-history tools
    /// dedupe by reference: with a singleton, every click stomped the
    /// previous selection's data, so navigating "back" in those tools
    /// showed whatever the latest click was. With the ring, each historical
    /// selection survives until <c>PoolSize</c> later clicks of the same
    /// kind push it out — well past the depth of any reasonable history UI.
    ///
    /// The pool also has a side benefit: every click changes the
    /// <see cref="Selection.activeObject"/> reference, so Unity's
    /// <c>Selection.selectionChanged</c> always fires (it used to skip
    /// same-kind clicks because the reference matched), letting the
    /// hierarchy window drop a custom <c>SelectionRequested</c> event.
    ///
    /// MultiSelect stays singleton — it carries only a count, has no per-
    /// selection identity worth preserving, and history tools don't navigate
    /// to it.
    /// </summary>
    public static class TrecsSelectionProxies
    {
        const int PoolSize = 16;

        static readonly Pool<TrecsEntitySelection> _entityPool = new("Trecs Entity");
        static readonly Pool<TrecsTemplateSelection> _templatePool = new("Trecs Template");
        static readonly Pool<TrecsComponentTypeSelection> _componentTypePool = new(
            "Trecs Component"
        );
        static readonly Pool<TrecsAccessorSelection> _accessorPool = new("Trecs Accessor");
        static readonly Pool<TrecsSetSelection> _setPool = new("Trecs Set");
        static readonly Pool<TrecsTagSelection> _tagPool = new("Trecs Tag");
        static TrecsMultiSelection _multi;

        public static TrecsEntitySelection NextEntity() => _entityPool.Next();

        public static TrecsTemplateSelection NextTemplate() => _templatePool.Next();

        public static TrecsComponentTypeSelection NextComponentType() => _componentTypePool.Next();

        public static TrecsAccessorSelection NextAccessor() => _accessorPool.Next();

        public static TrecsSetSelection NextSet() => _setPool.Next();

        public static TrecsTagSelection NextTag() => _tagPool.Next();

        public static TrecsMultiSelection MultiSelect
        {
            get
            {
                if (_multi == null)
                {
                    _multi = ScriptableObject.CreateInstance<TrecsMultiSelection>();
                    _multi.hideFlags = HideFlags.DontSave;
                    _multi.name = "Trecs Multi-Select";
                }
                return _multi;
            }
        }

        sealed class Pool<T>
            where T : ScriptableObject
        {
            readonly T[] _slots = new T[PoolSize];
            readonly string _displayName;
            int _next;

            public Pool(string displayName)
            {
                _displayName = displayName;
            }

            public T Next()
            {
                int idx = _next;
                _next = (_next + 1) % PoolSize;
                ref T slot = ref _slots[idx];
                if (slot == null)
                {
                    slot = ScriptableObject.CreateInstance<T>();
                    slot.hideFlags = HideFlags.DontSave;
                    slot.name = _displayName;
                }
                return slot;
            }
        }
    }
}
