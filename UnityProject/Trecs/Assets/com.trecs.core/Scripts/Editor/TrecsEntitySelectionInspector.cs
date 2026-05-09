using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Trecs.Internal;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Trecs
{
    [CustomEditor(typeof(TrecsEntitySelection))]
    public sealed class TrecsEntitySelectionInspector : Editor
    {
        [InitializeOnLoadMethod]
        static void RegisterEditorAccessorName() =>
            TrecsEditorAccessorNames.Register("TrecsEntitySelectionInspector");

        WorldAccessor _accessor;
        ResolvedTemplate _currentTemplate;
        TrecsEntityInspectorBuffer _buffer;

        // Old buffers from earlier template-change rebuilds. Kept alive until
        // OnDisable so any pending IMGUI events that still reference their
        // SerializedProperty paths through PropertyHandlerCache find a valid
        // target instead of a destroyed/shrunken one.
        readonly List<TrecsEntityInspectorBuffer> _bufferGraveyard = new();
        SerializedObject _serializedObject;
        SerializedProperty _boxesProp;
        bool _suppressWriteBack;

        VisualElement _root;
        VisualElement _bodyContainer;
        VisualElement _componentsContainer;
        Label _statusLabel;
        Label _headerLabel;
        Label _templateLabel;
        Label _worldLabel;
        VisualElement _tagsRow;
        int _lastTagsHash;

        // Per-row readonly refreshers for components that bypass the
        // SerializeReference path (generic structs like Interpolated<T> that
        // Unity's serializer can't navigate). Each entry reads the boxed
        // value via WorldAccessor and pushes it into a reflection-driven
        // readonly UI. Cleared on every template rebuild.
        readonly List<Action<EntityIndex>> _unsupportedRowRefreshers = new();

        void OnDisable()
        {
            // Drop refs first so any pending PropertyField redraws find a
            // null SerializedObject instead of a dangling one mid-teardown.
            _serializedObject = null;
            _boxesProp = null;
            if (_buffer != null)
            {
                DestroyImmediate(_buffer);
                _buffer = null;
            }
            for (int i = 0; i < _bufferGraveyard.Count; i++)
            {
                if (_bufferGraveyard[i] != null)
                {
                    DestroyImmediate(_bufferGraveyard[i]);
                }
            }
            _bufferGraveyard.Clear();
        }

        public override VisualElement CreateInspectorGUI()
        {
            _root = new VisualElement();
            _root.style.paddingLeft = 8;
            _root.style.paddingRight = 8;
            _root.style.paddingTop = 8;
            _root.style.paddingBottom = 8;

            _statusLabel = new Label();
            _statusLabel.style.opacity = 0.7f;
            _statusLabel.style.whiteSpace = WhiteSpace.Normal;
            _statusLabel.style.display = DisplayStyle.None;
            _root.Add(_statusLabel);

            _bodyContainer = new VisualElement();
            BuildStaticHeader(_bodyContainer);
            _root.Add(_bodyContainer);

            Refresh();

            var refreshMs = TrecsDebugWindowSettings.Get().RefreshIntervalMs;
            _root.schedule.Execute(Refresh).Every(refreshMs);

            return _root;
        }

        void BuildStaticHeader(VisualElement container)
        {
            _headerLabel = new Label();
            _headerLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _headerLabel.style.fontSize = 13;
            container.Add(_headerLabel);

            // Shift+hover the heading → hierarchy scrolls back to this
            // entity's tree row.
            TrecsInspectorLinks.WireHoverPreviewEntity(
                _headerLabel,
                () =>
                {
                    var sel = target as TrecsEntitySelection;
                    return (sel?.GetWorld(), sel?.Handle ?? default);
                }
            );

            // Template label is clickable — switches the active selection to
            // the corresponding template inspector. Closure reads
            // _currentTemplate at click time, so the navigation always
            // points at the entity's current template (which can change as
            // the entity moves between groups).
            _templateLabel = TrecsInspectorLinks.MakeLink(
                string.Empty,
                () =>
                {
                    var sel = (TrecsEntitySelection)target;
                    var w = sel?.GetWorld();
                    if (w != null && _currentTemplate != null)
                    {
                        TrecsInspectorLinks.SelectTemplate(w, _currentTemplate.Template);
                    }
                }
            );
            _templateLabel.style.opacity = 0.85f;
            container.Add(_templateLabel);

            _worldLabel = new Label();
            _worldLabel.style.opacity = 0.6f;
            container.Add(_worldLabel);

            _tagsRow = new VisualElement();
            _tagsRow.style.flexDirection = FlexDirection.Row;
            _tagsRow.style.flexWrap = Wrap.Wrap;
            _tagsRow.style.alignItems = Align.Center;
            container.Add(_tagsRow);

            var componentsHeader = new Label("Components");
            componentsHeader.style.unityFontStyleAndWeight = FontStyle.Bold;
            componentsHeader.style.marginTop = 10;
            container.Add(componentsHeader);

            _componentsContainer = new VisualElement();
            container.Add(_componentsContainer);
        }

        void Refresh()
        {
            var selection = (TrecsEntitySelection)target;
            if (selection == null)
            {
                return;
            }

            var world = selection.GetWorld();
            if (world == null || world.IsDisposed)
            {
                ShowStatus("World is no longer available.");
                return;
            }

            if (!selection.Handle.TryToIndex(world, out var entityIndex))
            {
                ShowStatus(
                    $"Entity id:{selection.Handle.UniqueId} v:{selection.Handle.Version} no longer exists."
                );
                return;
            }

            try
            {
                _accessor ??= world.CreateAccessor(
                    AccessorRole.Unrestricted,
                    "TrecsEntitySelectionInspector"
                );
            }
            catch (Exception e)
            {
                ShowStatus($"Failed to create world accessor: {e.Message}");
                return;
            }

            ResolvedTemplate rt;
            try
            {
                rt = world.WorldInfo.GetResolvedTemplateForGroup(entityIndex.GroupIndex);
            }
            catch (Exception e)
            {
                ShowStatus($"Failed to resolve template: {e.Message}");
                return;
            }

            _statusLabel.style.display = DisplayStyle.None;
            _bodyContainer.style.display = DisplayStyle.Flex;

            UpdateHeader(selection, world, rt, entityIndex.GroupIndex);

            if (rt != _currentTemplate)
            {
                _currentTemplate = rt;
                RebuildComponentBoxes(selection, rt);
            }

            ReadAllComponentsFromWorld(selection, entityIndex);
            _serializedObject?.Update();
        }

        void UpdateHeader(
            TrecsEntitySelection selection,
            World world,
            ResolvedTemplate rt,
            GroupIndex group
        )
        {
            _headerLabel.text =
                $"Entity id:{selection.Handle.UniqueId} v:{selection.Handle.Version}";
            _templateLabel.text = $"Template: {rt.DebugName}";
            _worldLabel.text = $"World: {world.DebugName ?? "(unnamed)"}";

            var tags = world.WorldInfo.GetGroupTags(group);
            // Tags are reasonably stable per group, but a structural change
            // can swap which tags apply (e.g. partition move). Rebuild the
            // row only if the set actually differs to avoid hover-state churn.
            int hash = tags.Count;
            foreach (var t in tags)
            {
                hash ^= t.Guid;
            }
            if (hash == _lastTagsHash)
            {
                return;
            }
            _lastTagsHash = hash;
            _tagsRow.Clear();
            if (tags.Count == 0)
            {
                _tagsRow.style.display = DisplayStyle.None;
                return;
            }
            _tagsRow.style.display = DisplayStyle.Flex;
            var prefix = new Label("Tags: ");
            prefix.style.opacity = 0.6f;
            prefix.style.marginRight = 2;
            prefix.style.alignSelf = Align.Center;
            _tagsRow.Add(prefix);
            foreach (var tag in tags)
            {
                var captured = tag;
                _tagsRow.Add(
                    TrecsInspectorLinks.MakeTagLink(
                        world,
                        captured,
                        captured.ToString() ?? $"#{captured.Guid}"
                    )
                );
            }
        }

        void ShowStatus(string text)
        {
            _statusLabel.text = text;
            _statusLabel.style.display = DisplayStyle.Flex;
            _bodyContainer.style.display = DisplayStyle.None;
            _currentTemplate = null;
            _serializedObject = null;
            _boxesProp = null;
            if (_componentsContainer != null)
            {
                _componentsContainer.Clear();
            }
        }

        void RebuildComponentBoxes(TrecsEntitySelection selection, ResolvedTemplate rt)
        {
            // Mint a brand-new buffer for this template instead of reusing
            // the previous one — Unity's PropertyHandlerCache is keyed on
            // (target, propertyPath) and shrinking the same target's
            // [SerializeReference] list would leave stale handlers pointing
            // at indices that no longer exist. The previous buffer lingers
            // in the graveyard so any pending IMGUI events that still query
            // its old handlers find the box list they expect.
            if (_buffer != null)
            {
                _bufferGraveyard.Add(_buffer);
            }
            _buffer = CreateInstance<TrecsEntityInspectorBuffer>();
            _buffer.hideFlags = HideFlags.DontSave;
            _buffer.name = "Trecs Entity Inspector Buffer";

            // Boxes are constructed reflectively so the concrete generic
            // instantiation matches each component type — Unity's serializer
            // treats each one as a distinct managed reference type for
            // SerializedProperty navigation.

            // Two parallel structures:
            //   _buffer.ComponentBoxes — only contains real, fully-serializable
            //     TrecsComponentBox<T> instances. NEVER nulls or unsupported
            //     entries; that keeps Unity's [SerializeReference] managed-ref
            //     table contiguous, otherwise even valid trailing indices
            //     trigger "out of bounds offset" log spam during
            //     PropertyHandlerCache lookup.
            //   rows — one entry per ComponentDeclaration in display order.
            //     BoxIndex is the index into ComponentBoxes for supported
            //     components, or -1 for unsupported (rendered as a label).
            var rows = new List<ComponentRowInfo>(rt.ComponentDeclarations.Count);
            for (int i = 0; i < rt.ComponentDeclarations.Count; i++)
            {
                var decl = rt.ComponentDeclarations[i];
                var t = decl.ComponentType;

                TrecsComponentBoxBase box = null;
                // Skip components that contain types Unity's serializer can't
                // navigate into — generic structs (Interpolated<T>,
                // NativeSharedPtr<T>, FixedArray<T>, BlobAssetReference<T>,
                // etc.), whether at the top level or nested inside any field.
                // These leave incomplete managed-ref entries that corrupt
                // PropertyHandlerCache hashing for the trailing array index
                // and produce "out of bounds offset" log spam every IMGUI
                // redraw, even though our valueProp != null heuristic still
                // sees them as healthy. The skip cascades to the
                // <unsupported> render path.
                if (!HasUnsupportedSerializationField(t))
                {
                    try
                    {
                        var boxType = typeof(TrecsComponentBox<>).MakeGenericType(t);
                        box = (TrecsComponentBoxBase)Activator.CreateInstance(boxType);
                    }
                    catch (Exception)
                    {
                        // Stays null; row gets BoxIndex = -1 below.
                    }
                }

                int boxIndex;
                if (box != null)
                {
                    boxIndex = _buffer.ComponentBoxes.Count;
                    _buffer.ComponentBoxes.Add(box);
                }
                else
                {
                    boxIndex = -1;
                }

                var (isUnwrap, unwrapField) = GetUnwrapInfo(t);
                rows.Add(
                    new ComponentRowInfo
                    {
                        BoxIndex = boxIndex,
                        ComponentType = t,
                        DisplayName = ComputeDisplayName(t),
                        DeclaringTemplate = FindDeclaringTemplate(rt, t),
                        IsUnwrap = isUnwrap,
                        UnwrapFieldName = unwrapField,
                    }
                );
            }

            _serializedObject = new SerializedObject(_buffer);
            _serializedObject.Update();
            _boxesProp = _serializedObject.FindProperty(
                nameof(TrecsEntityInspectorBuffer.ComponentBoxes)
            );

            _componentsContainer.Clear();
            _unsupportedRowRefreshers.Clear();

            // Group by declaring template; within each group sort
            // alphabetically by display name. Skip the outer template foldout
            // when only one template owns everything — keeps the UI flat for
            // simple cases.
            var groups = rows.GroupBy(r => r.DeclaringTemplate)
                .OrderBy(g => g.Key?.DebugName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var useTemplateGroups = groups.Count > 1;

            foreach (var group in groups)
            {
                VisualElement parent;
                if (useTemplateGroups)
                {
                    var templateFoldout = new Foldout
                    {
                        text = group.Key?.DebugName ?? "(unknown template)",
                        value = true,
                    };
                    templateFoldout.style.marginTop = 4;
                    var label = templateFoldout.Q<Label>(className: Foldout.textUssClassName);
                    if (label != null)
                    {
                        label.style.unityFontStyleAndWeight = FontStyle.Bold;
                    }
                    _componentsContainer.Add(templateFoldout);
                    parent = templateFoldout;
                }
                else
                {
                    parent = _componentsContainer;
                }

                var sorted = group
                    .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                foreach (var row in sorted)
                {
                    RenderComponentRow(parent, row, selection);
                }
            }
            // Each PropertyField is BindProperty'd individually inside
            // RenderComponentRow — we used to also call
            // _componentsContainer.Bind(_serializedObject) here but the
            // double-bind appears to trigger spurious PropertyHandlerCache
            // hash computations on the last [SerializeReference] element
            // ("out of bounds offset" log spam), so don't.
        }

        void RenderComponentRow(
            VisualElement parent,
            ComponentRowInfo row,
            TrecsEntitySelection selection
        )
        {
            var world = selection.GetWorld();

            // Unsupported components (generic types Unity can't serialize)
            // never get a box, so they render without touching the SO.
            if (row.BoxIndex < 0)
            {
                AddUnsupportedRow(parent, row, world, "<unsupported>");
                return;
            }

            var box = _buffer.ComponentBoxes[row.BoxIndex];
            if (box == null)
            {
                AddUnsupportedRow(parent, row, world, "<unsupported>");
                return;
            }

            var elementProp = _boxesProp.GetArrayElementAtIndex(row.BoxIndex);
            var valueProp = elementProp.FindPropertyRelative(
                TrecsComponentBoxBase.ValuePropertyName
            );
            if (valueProp == null)
            {
                AddUnsupportedRow(parent, row, world, "<no SerializedProperty>");
                return;
            }

            // [Unwrap] components have a single instance field — render that
            // field inline with the component's display name as its label
            // (no extra "Value" foldout layer, matching how the source-gen
            // collapses the type for aspect property names). Walk through
            // chains of [Unwrap] types so e.g. Interpolated<CPosition> →
            // CPosition → float3 collapses entirely down to the float3 row,
            // since Unity's PropertyField doesn't know about [Unwrap] and
            // would otherwise leave nested "Value" labels visible.
            if (row.IsUnwrap && row.UnwrapFieldName != null)
            {
                var inner = valueProp;
                var innerType = row.ComponentType;
                int hops = 0;
                while (hops < 5)
                {
                    var (subUnwrap, subFieldName) = GetUnwrapInfo(innerType);
                    if (!subUnwrap || subFieldName == null)
                    {
                        break;
                    }
                    var next = inner.FindPropertyRelative(subFieldName);
                    if (next == null)
                    {
                        break;
                    }
                    var subFieldInfo = innerType.GetField(
                        subFieldName,
                        BindingFlags.Public | BindingFlags.Instance
                    );
                    if (subFieldInfo == null)
                    {
                        break;
                    }
                    inner = next;
                    innerType = subFieldInfo.FieldType;
                    hops++;
                }
                if (hops > 0)
                {
                    var pf = new PropertyField(inner, row.DisplayName);
                    pf.BindProperty(inner);
                    var bi = row.BoxIndex;
                    pf.RegisterValueChangeCallback(_ => OnBoxValueChanged(bi));
                    parent.Add(WrapWithJumpIcon(pf, world, row.ComponentType));
                    return;
                }
                // Fall through to default foldout if Unity didn't surface a
                // SerializedProperty for the inner field (shouldn't happen
                // for [Serializable] components, but be defensive).
            }

            // Skip the outer Foldout we used to wrap around: PropertyField
            // already creates its own foldout for the SerializeReference
            // value, and stacking another wrapper above it just adds a
            // redundant "Value" layer between the component name and the
            // actual fields. Passing row.DisplayName as the label gets the
            // PropertyField's own foldout titled with the component name.
            var pfBase = new PropertyField(valueProp, row.DisplayName);
            pfBase.style.marginTop = 1;
            pfBase.BindProperty(valueProp);
            var capturedIndex = row.BoxIndex;
            pfBase.RegisterValueChangeCallback(_ => OnBoxValueChanged(capturedIndex));
            parent.Add(WrapWithJumpIcon(pfBase, world, row.ComponentType));
        }

        // Adds a small "↗" jump icon to the right of the component header.
        // Hover the icon → preview the matching component-type row in the
        // hierarchy; click → select it.
        VisualElement WrapWithJumpIcon(VisualElement child, World world, Type componentType)
        {
            if (world == null || componentType == null)
            {
                return child;
            }
            var rowContainer = new VisualElement();
            rowContainer.style.flexDirection = FlexDirection.Row;
            rowContainer.style.alignItems = Align.FlexStart;
            child.style.flexGrow = 1;
            rowContainer.Add(child);
            rowContainer.Add(TrecsInspectorLinks.MakeComponentTypeJumpButton(world, componentType));
            return rowContainer;
        }

        void AddUnsupportedRow(
            VisualElement parent,
            ComponentRowInfo row,
            World world,
            string suffix
        )
        {
            // Generic struct components (Interpolated<T>, InterpolatedPrevious<T>,
            // NativeSharedPtr<T>, FixedArray<T>, ...) can't go through Unity's
            // SerializeReference/PropertyField path — its PropertyHandlerCache
            // mishashes nested generic-struct fields and floods the console with
            // "out of bounds offset" log spam. We can still pull the value with
            // WorldAccessor.ReadComponentBoxed and walk it reflectively, so we
            // render each field as a readonly Label. Editing isn't supported
            // (there's no SerializedProperty path back into the value) but the
            // user can at least see what's there.
            var foldout = new Foldout { text = row.DisplayName, value = false };
            foldout.style.marginTop = 1;
            var content = new VisualElement();
            content.style.marginLeft = 12;
            foldout.Add(content);

            var hint = new Label($"(readonly: {suffix})");
            hint.style.opacity = 0.5f;
            hint.style.fontSize = 10;
            hint.style.marginBottom = 2;
            content.Add(hint);

            var updater = BuildReadonlyValueDisplay(content, row.ComponentType, depth: 0);

            var capturedType = row.ComponentType;
            _unsupportedRowRefreshers.Add(entityIndex =>
            {
                object boxed;
                try
                {
                    boxed = _accessor.ReadComponentBoxed(entityIndex, capturedType);
                }
                catch
                {
                    return;
                }
                updater(boxed);
            });

            parent.Add(WrapWithJumpIcon(foldout, world, row.ComponentType));
        }

        // Builds a readonly UI tree for `type` under `container` and returns
        // an updater that takes a boxed instance of `type` and writes its
        // values into the labels. Recurses into nested structs without a
        // useful ToString; bottoms out at primitives, enums, and types with
        // a custom ToString (Vector*, Quaternion, float3, etc.).
        static Action<object> BuildReadonlyValueDisplay(
            VisualElement container,
            Type type,
            int depth
        )
        {
            // Apply [Unwrap] transparently so wrapper chains like
            // Interpolated<CPosition> → CPosition → float3 don't render as
            // three nested "Value" foldouts. Collect the field chain so the
            // updater can drill the boxed value down to the inner type.
            List<FieldInfo> unwrapChain = null;
            while (
                depth + (unwrapChain?.Count ?? 0) < 6
                && IsUnwrapWithSingleField(type, out var unwrapField)
            )
            {
                (unwrapChain ??= new List<FieldInfo>()).Add(unwrapField);
                type = unwrapField.FieldType;
            }

            Action<object> innerUpdater = BuildLeafOrFieldsDisplay(container, type, depth);

            if (unwrapChain == null)
            {
                return innerUpdater;
            }

            var capturedChain = unwrapChain;
            return boxed =>
            {
                object current = boxed;
                for (int i = 0; current != null && i < capturedChain.Count; i++)
                {
                    try
                    {
                        current = capturedChain[i].GetValue(current);
                    }
                    catch
                    {
                        current = null;
                        break;
                    }
                }
                innerUpdater(current);
            };
        }

        static Action<object> BuildLeafOrFieldsDisplay(
            VisualElement container,
            Type type,
            int depth
        )
        {
            if (depth >= 6 || HasUsefulInlineRepresentation(type) || !type.IsValueType)
            {
                var label = new Label();
                label.style.flexGrow = 1;
                label.style.whiteSpace = WhiteSpace.Normal;
                label.style.opacity = 0.75f;
                container.Add(label);
                return boxed => label.text = FormatReadonlyValue(boxed);
            }

            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            if (fields.Length == 0)
            {
                var emptyLabel = new Label("(empty struct)");
                emptyLabel.style.opacity = 0.5f;
                container.Add(emptyLabel);
                return _ => { };
            }

            var updaters = new List<Action<object>>(fields.Length);
            foreach (var field in fields)
            {
                var capturedField = field;

                var fieldRow = new VisualElement();
                fieldRow.style.flexDirection = FlexDirection.Row;
                fieldRow.style.alignItems = Align.FlexStart;
                var nameLabel = new Label(ObjectNames.NicifyVariableName(field.Name));
                nameLabel.style.minWidth = 120;
                nameLabel.style.opacity = 0.85f;
                fieldRow.Add(nameLabel);
                var valueContainer = new VisualElement();
                valueContainer.style.flexGrow = 1;
                fieldRow.Add(valueContainer);
                container.Add(fieldRow);

                var subUpdater = BuildReadonlyValueDisplay(
                    valueContainer,
                    field.FieldType,
                    depth + 1
                );
                updaters.Add(boxedParent =>
                {
                    if (boxedParent == null)
                    {
                        subUpdater(null);
                        return;
                    }
                    object fieldValue;
                    try
                    {
                        fieldValue = capturedField.GetValue(boxedParent);
                    }
                    catch
                    {
                        return;
                    }
                    subUpdater(fieldValue);
                });
            }

            return boxed =>
            {
                for (int i = 0; i < updaters.Count; i++)
                {
                    updaters[i](boxed);
                }
            };
        }

        static bool IsUnwrapWithSingleField(Type type, out FieldInfo field)
        {
            field = null;
            if (type == null || !type.IsValueType)
            {
                return false;
            }
            if (type.GetCustomAttribute<UnwrapAttribute>(false) == null)
            {
                return false;
            }
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            if (fields.Length != 1)
            {
                return false;
            }
            field = fields[0];
            return true;
        }

        static bool HasUsefulInlineRepresentation(Type t)
        {
            if (t == null)
            {
                return true;
            }
            if (t.IsPrimitive || t.IsEnum || t == typeof(string))
            {
                return true;
            }
            // Types with a custom ToString override render meaningfully via
            // .ToString() — covers UnityEngine.Vector*/Color/Quaternion,
            // Unity.Mathematics.float*/int*, etc.
            var m = t.GetMethod("ToString", Type.EmptyTypes);
            return m != null
                && m.DeclaringType != typeof(object)
                && m.DeclaringType != typeof(ValueType);
        }

        static string FormatReadonlyValue(object value)
        {
            if (value == null)
            {
                return "null";
            }
            return value.ToString();
        }

        // ----- Display-name + grouping helpers --------------------------------

        class ComponentRowInfo
        {
            public int BoxIndex;
            public Type ComponentType;
            public string DisplayName;
            public Template DeclaringTemplate;
            public bool IsUnwrap;
            public string UnwrapFieldName;
        }

        static readonly ConcurrentDictionary<Assembly, string> _componentPrefixCache = new();
        static readonly ConcurrentDictionary<
            Type,
            (bool isUnwrap, string fieldName)
        > _unwrapInfoCache = new();

        static string GetComponentPrefix(Assembly asm)
        {
            return _componentPrefixCache.GetOrAdd(
                asm,
                static a =>
                {
                    var attr = a.GetCustomAttribute<TrecsSourceGenSettingsAttribute>();
                    return attr?.ComponentPrefix ?? string.Empty;
                }
            );
        }

        static readonly ConcurrentDictionary<Type, bool> _unsupportedFieldCache = new();

        /// <summary>
        /// Returns true if the given type, or any field type reachable from
        /// it, is a generic struct (NativeSharedPtr&lt;T&gt;, FixedArray&lt;T&gt;,
        /// BlobAssetReference&lt;T&gt;, Interpolated&lt;T&gt; etc.). Unity's
        /// serializer can't navigate generic struct fields, and our
        /// experience is that having even one such field anywhere in a
        /// component leaves an incomplete managed-ref entry that triggers
        /// "Cannot get managed reference index with out of bounds offset"
        /// log spam during IMGUI hashing, even though valueProp itself is
        /// non-null.
        /// </summary>
        static bool HasUnsupportedSerializationField(Type t)
        {
            return _unsupportedFieldCache.GetOrAdd(
                t,
                static type => Walk(type, new HashSet<Type>())
            );

            static bool Walk(Type type, HashSet<Type> visited)
            {
                if (type == null)
                {
                    return false;
                }
                if (type.IsPrimitive || type.IsEnum || type == typeof(string))
                {
                    return false;
                }
                if (!type.IsValueType)
                {
                    // Reference types (Unity.Object, etc.) — Unity treats
                    // these as serialized references; not the trigger we're
                    // chasing.
                    return false;
                }
                // Closed-generic value types — Nullable<T>, Interpolated<T>,
                // NativeSharedPtr<T>, FixedArray<T>, BlobAssetReference<T>,
                // etc. Unity's [SerializeField] serializer and PropertyField
                // can't navigate generic struct fields: it leaves an
                // incomplete managed-reference entry that corrupts
                // PropertyHandlerCache hashing for the trailing
                // [SerializeReference] array index, producing "Cannot get
                // managed reference index with out of bounds offset" log
                // spam every IMGUI redraw. The [Serializable] check below
                // doesn't catch this — Nullable<T> in particular carries
                // [Serializable] but exposes zero public instance fields, so
                // the field-walk silently bottoms out and reports the
                // containing component as supported. Route the whole
                // component through the readonly fallback renderer instead.
                if (type.IsGenericType)
                {
                    return true;
                }
                // Custom struct: Unity's serializer requires [Serializable]
                // to navigate into its fields. Without it, Unity can't
                // expand the field's children — leaving a half-formed
                // managed-ref entry that breaks PropertyHandlerCache hashing
                // for the trailing array index. Built-in Unity types
                // (Vector*/Color/Quaternion/etc.) and Unity.Mathematics
                // types already carry [Serializable], so this rejects only
                // custom structs (e.g. svkj-physics CollisionFilter,
                // Material) that nobody has marked.
                if (!type.IsDefined(typeof(SerializableAttribute), inherit: false))
                {
                    return true;
                }
                if (!visited.Add(type))
                {
                    return false;
                }
                foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (Walk(f.FieldType, visited))
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        static (bool isUnwrap, string fieldName) GetUnwrapInfo(Type t)
        {
            return _unwrapInfoCache.GetOrAdd(
                t,
                static type =>
                {
                    var hasUnwrap = type.GetCustomAttribute<UnwrapAttribute>(false) != null;
                    if (!hasUnwrap)
                    {
                        return (false, null);
                    }
                    var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
                    if (fields.Length != 1)
                    {
                        // [Unwrap] requires exactly one instance field; bail
                        // back to the foldout view if the type doesn't fit.
                        return (false, null);
                    }
                    return (true, fields[0].Name);
                }
            );
        }

        static string ComputeDisplayName(Type t)
        {
            // Non-generic: strip the per-assembly component prefix (e.g. "C"
            // from "CHealth") and nicify ("CHealth" → "Health").
            if (!t.IsGenericType)
            {
                var name = StripComponentPrefix(t.Name, t.Assembly);
                return ObjectNames.NicifyVariableName(name);
            }
            // Generic: PrettyTypeNameCache produces "Outer<Inner1, Inner2>"
            // formatting; we apply prefix-strip + nicification only to the
            // outer base name (preserving the angle-bracket type args, which
            // are themselves type names and shouldn't be space-split).
            var pretty = t.GetPrettyName();
            var ltIdx = pretty.IndexOf('<');
            if (ltIdx <= 0)
            {
                return pretty;
            }
            var baseName = StripComponentPrefix(pretty.Substring(0, ltIdx), t.Assembly);
            return ObjectNames.NicifyVariableName(baseName) + pretty.Substring(ltIdx);
        }

        static string StripComponentPrefix(string name, Assembly asm)
        {
            var prefix = GetComponentPrefix(asm);
            if (
                !string.IsNullOrEmpty(prefix)
                && name.StartsWith(prefix, StringComparison.Ordinal)
                && name.Length > prefix.Length
            )
            {
                return name.Substring(prefix.Length);
            }
            return name;
        }

        static Template FindDeclaringTemplate(ResolvedTemplate rt, Type componentType)
        {
            // Leaf-first walk: if the leaf and a base both declare the same
            // component type (override), the leaf wins — that's where the
            // override is authored, so it's the natural grouping.
            foreach (var dec in rt.Template.LocalComponentDeclarations)
            {
                if (dec.ComponentType == componentType)
                {
                    return rt.Template;
                }
            }
            foreach (var bt in rt.AllBaseTemplates)
            {
                foreach (var dec in bt.LocalComponentDeclarations)
                {
                    if (dec.ComponentType == componentType)
                    {
                        return bt;
                    }
                }
            }
            return rt.Template;
        }

        void ReadAllComponentsFromWorld(TrecsEntitySelection selection, EntityIndex entityIndex)
        {
            if (_buffer == null)
            {
                return;
            }
            // Suppress write-back during the world→box copy: any
            // RegisterValueChangeCallback that fires off a SerializedObject
            // refresh would otherwise round-trip the just-read world value
            // back into the world (a no-op write, but a wasteful one).
            _suppressWriteBack = true;
            try
            {
                for (int i = 0; i < _buffer.ComponentBoxes.Count; i++)
                {
                    var box = _buffer.ComponentBoxes[i];
                    if (box == null)
                    {
                        continue;
                    }
                    try
                    {
                        var boxed = _accessor.ReadComponentBoxed(entityIndex, box.ComponentType);
                        box.ReadFromBoxed(boxed);
                    }
                    catch
                    {
                        // Skip; the property will keep its previous value.
                    }
                }
            }
            finally
            {
                _suppressWriteBack = false;
            }

            // Refresh readonly rows for components that bypass the
            // SerializeReference path (Interpolated<T> et al.).
            for (int i = 0; i < _unsupportedRowRefreshers.Count; i++)
            {
                try
                {
                    _unsupportedRowRefreshers[i](entityIndex);
                }
                catch
                {
                    // Skip; row keeps previous values.
                }
            }
        }

        void OnBoxValueChanged(int boxIndex)
        {
            if (_suppressWriteBack || _buffer == null)
            {
                return;
            }
            var selection = (TrecsEntitySelection)target;
            if (selection == null)
            {
                return;
            }
            if (boxIndex < 0 || boxIndex >= _buffer.ComponentBoxes.Count)
            {
                return;
            }
            var box = _buffer.ComponentBoxes[boxIndex];
            if (box == null)
            {
                return;
            }
            var world = selection.GetWorld();
            if (world == null || world.IsDisposed || _accessor == null)
            {
                return;
            }
            if (!selection.Handle.TryToIndex(world, out var entityIndex))
            {
                return;
            }
            try
            {
                _accessor.WriteComponentBoxed(entityIndex, box.ComponentType, box.ReadAsBoxed());
            }
            catch
            {
                // Failed write — next refresh tick will re-read from world
                // and clobber any stale local edit.
            }
        }
    }
}
