using System;
using System.Collections.Generic;
using UnityEditor.UIElements;
using UnityEngine;

namespace Trecs
{
    /// <summary>
    /// Per-Editor buffer ScriptableObject that hosts the
    /// <see cref="ComponentBoxes"/> list bound by
    /// <see cref="TrecsEntitySelectionInspector"/>. Created in
    /// <see cref="UnityEditor.Editor.OnEnable"/>, destroyed in
    /// <see cref="UnityEditor.Editor.OnDisable"/>: each Inspector instance gets
    /// its own target, so Unity's <c>PropertyHandlerCache</c> entries (keyed on
    /// target + path) cannot leak across selections of differently-shaped
    /// templates and trigger "out of bounds offset" log spam when a shorter
    /// list is queried for a path from a longer one.
    ///
    /// Lives in its own file (matching the class name) so Unity's asset
    /// database can resolve a script asset for it — without that, every
    /// inspector selection logs "No script asset for TrecsEntityInspectorBuffer.
    /// Check that the definition is in a file of the same name."
    /// </summary>
    public sealed class TrecsEntityInspectorBuffer : ScriptableObject
    {
        // [SerializeReference] holds the polymorphic generic boxes — one per
        // component on the resolved template — so a single non-generic SO can
        // address each component's value through SerializedProperty paths and
        // hand them to PropertyField for native Inspector-style rendering.
        [SerializeReference]
        public List<TrecsComponentBoxBase> ComponentBoxes = new();
    }

    /// <summary>
    /// Type-erased base for <see cref="TrecsComponentBox{T}"/> so a single list
    /// can hold concrete generic boxes for any component type encountered at
    /// runtime.
    /// </summary>
    [Serializable]
    public abstract class TrecsComponentBoxBase
    {
        // The SerializedProperty path inside each box element. Must match the
        // field name on TrecsComponentBox<T> below — kept as a const so the
        // editor doesn't need a generic type instantiation just to get the
        // name (and Trecs's IEntityComponent source generator can't follow a
        // dummy IEntityComponent into a non-partial outer class).
        public const string ValuePropertyName = "Value";

        public abstract Type ComponentType { get; }
        public abstract void ReadFromBoxed(object boxed);
        public abstract object ReadAsBoxed();
    }

    /// <summary>
    /// One box per component type holds the live component value as a regular
    /// <c>[SerializeField]</c>, so Unity's serialization machinery and
    /// <see cref="PropertyField"/> renders it identically to any other
    /// <c>[SerializeField] T</c> on a MonoBehaviour or ScriptableObject.
    /// </summary>
    [Serializable]
    public sealed class TrecsComponentBox<T> : TrecsComponentBoxBase
        where T : unmanaged, IEntityComponent
    {
        [SerializeField]
        public T Value;

        public override Type ComponentType => typeof(T);

        public override void ReadFromBoxed(object boxed) => Value = (T)boxed;

        public override object ReadAsBoxed() => Value;
    }
}
