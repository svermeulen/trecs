#nullable enable

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Trecs.SourceGen.Aspect;
using Trecs.SourceGen.Performance;

namespace Trecs.SourceGen.Shared
{
    internal enum FromWorldFieldKind
    {
        Unsupported,
        NativeComponentBufferRead,
        NativeComponentBufferWrite,
        NativeComponentRead,
        NativeComponentWrite,
        NativeComponentLookupRead,
        NativeComponentLookupWrite,
        NativeSetCommandBuffer,
        NativeEntitySetIndices,
        NativeSetRead,
        NativeFactory,
        NativeWorldAccessor,
        GroupIndex,
        NativeEntityHandleBuffer,
    }

    internal class FromWorldFieldInfo
    {
        public string FieldName { get; }
        public FromWorldFieldKind Kind { get; }
        public INamedTypeSymbol FieldType { get; }
        public ITypeSymbol? GenericArgument { get; } // null for non-generic types (NativeWorldAccessor, GroupIndex)

        /// <summary>
        /// Pre-parsed aspect data, populated only for <see cref="FromWorldFieldKind.NativeFactory"/>
        /// fields. Contains the read / write component lists used to emit per-(component, group)
        /// dep tracking and lookup creation.
        /// </summary>
        public AspectAttributeData? AspectData { get; }

        /// <summary>
        /// Inline tag types from [FromWorld(Tag = ...)] or [FromWorld(Tags = ...)].
        /// Null when no inline tags — the field generates a schedule-time parameter.
        /// Non-null (even if empty list, which shouldn't happen after validation) when
        /// inline tags are present — no schedule parameter is generated.
        /// </summary>
        public List<ITypeSymbol>? InlineTagTypes { get; }

        public FromWorldFieldInfo(
            string fieldName,
            FromWorldFieldKind kind,
            INamedTypeSymbol fieldType,
            ITypeSymbol? genericArgument,
            AspectAttributeData? aspectData = null,
            List<ITypeSymbol>? inlineTagTypes = null
        )
        {
            FieldName = fieldName;
            Kind = kind;
            FieldType = fieldType;
            GenericArgument = genericArgument;
            AspectData = aspectData;
            InlineTagTypes = inlineTagTypes;
        }
    }

    /// <summary>
    /// Per-field rendering helper for [FromWorld] field emission. Bundles every name
    /// and shape decision (schedule param, hoisted local, generic arg display) so the
    /// emitter doesn't have to recompute them per call site.
    /// </summary>
    internal class FromWorldFieldEmit
    {
        public FromWorldFieldKind Kind { get; }
        public string FieldName { get; }
        public string GenericArgDisplay { get; } // "" for non-generic types
        public ITypeSymbol? GenericArgSymbol { get; } // null for non-generic types
        public AspectAttributeData? AspectData { get; }
        public bool HasScheduleParam { get; }
        public string ScheduleParamType { get; } // "" when HasScheduleParam is false
        public string ScheduleParamName { get; } // "" when HasScheduleParam is false
        public bool IsOptionalParam { get; } // true when inline tags make the param optional (TagSet? = null)
        public bool NeedsHoistedSingleGroup { get; }
        public bool NeedsHoistedGroups { get; }
        public string HoistedSingleGroupLocal { get; }
        public string HoistedGroupsLocal { get; }

        /// <summary>
        /// The inline TagSet expression (e.g. "TagSet&lt;Fish&gt;.Value"), or "" when no inline tags.
        /// Used in EmitFromWorldHoistedSetup to combine with optional runtime tags.
        /// </summary>
        public string InlineTagSetExpression { get; }

        /// <summary>
        /// The expression that resolves to the final TagSet for this field's group(s).
        /// For fields with inline tags, this is a local variable that holds the combined result.
        /// For runtime-only fields, this is the schedule parameter name.
        /// Empty for fields that don't use TagSets (NativeComponentRead/Write, NativeSetCommandBuffer, NativeSetRead).
        /// </summary>
        public string TagSetExpression { get; }

        FromWorldFieldEmit(
            FromWorldFieldKind kind,
            string fieldName,
            string genericArgDisplay,
            ITypeSymbol? genericArgSymbol,
            AspectAttributeData? aspectData,
            bool hasScheduleParam,
            string scheduleParamType,
            string scheduleParamName,
            bool isOptionalParam,
            bool needsHoistedSingleGroup,
            bool needsHoistedGroups,
            string inlineTagSetExpression,
            string tagSetExpression
        )
        {
            Kind = kind;
            FieldName = fieldName;
            GenericArgDisplay = genericArgDisplay;
            GenericArgSymbol = genericArgSymbol;
            AspectData = aspectData;
            HasScheduleParam = hasScheduleParam;
            ScheduleParamType = scheduleParamType;
            ScheduleParamName = scheduleParamName;
            IsOptionalParam = isOptionalParam;
            NeedsHoistedSingleGroup = needsHoistedSingleGroup;
            NeedsHoistedGroups = needsHoistedGroups;
            HoistedSingleGroupLocal = needsHoistedSingleGroup
                ? "_trecs_" + LowerFirst(fieldName) + "_group"
                : "";
            HoistedGroupsLocal = needsHoistedGroups
                ? "_trecs_" + LowerFirst(fieldName) + "_groups"
                : "";
            InlineTagSetExpression = inlineTagSetExpression;
            TagSetExpression = tagSetExpression;
        }

        /// <summary>
        /// Builds a TagSet&lt;T1, T2, ...&gt;.Value expression from inline tag type symbols.
        /// </summary>
        static string BuildInlineTagSetExpression(List<ITypeSymbol> tagTypes)
        {
            var typeArgs = string.Join(
                ", ",
                tagTypes.Select(t => PerformanceCache.GetDisplayString(t))
            );
            return $"TagSet<{typeArgs}>.Value";
        }

        public static FromWorldFieldEmit Build(
            FromWorldFieldInfo info,
            bool suppressScheduleParam = false
        )
        {
            var result = BuildCore(info);
            if (suppressScheduleParam && result.HasScheduleParam)
            {
                return new FromWorldFieldEmit(
                    result.Kind,
                    result.FieldName,
                    result.GenericArgDisplay,
                    result.GenericArgSymbol,
                    result.AspectData,
                    hasScheduleParam: false,
                    scheduleParamType: "",
                    scheduleParamName: "",
                    isOptionalParam: false,
                    result.NeedsHoistedSingleGroup,
                    result.NeedsHoistedGroups,
                    result.InlineTagSetExpression,
                    result.TagSetExpression
                );
            }
            return result;
        }

        static FromWorldFieldEmit BuildCore(FromWorldFieldInfo info)
        {
            var t =
                info.GenericArgument != null
                    ? PerformanceCache.GetDisplayString(info.GenericArgument)
                    : "";
            var sym = info.GenericArgument;
            var lower = LowerFirst(info.FieldName);
            var hasInlineTags = info.InlineTagTypes != null;
            var inlineExpr = hasInlineTags ? BuildInlineTagSetExpression(info.InlineTagTypes!) : "";
            // When inline tags are present, the resolved TagSet is computed in a
            // local variable by EmitFromWorldHoistedSetup (combining inline + optional
            // runtime tags). Otherwise it's just the schedule param name.
            var resolvedTagsLocal = hasInlineTags ? "_trecs_" + lower + "_tags" : lower + "Tags";
            switch (info.Kind)
            {
                case FromWorldFieldKind.NativeComponentBufferRead:
                case FromWorldFieldKind.NativeComponentBufferWrite:
                    return new(
                        info.Kind,
                        info.FieldName,
                        t,
                        sym,
                        info.AspectData,
                        hasScheduleParam: true,
                        scheduleParamType: hasInlineTags ? "TagSet?" : "TagSet",
                        scheduleParamName: lower + "Tags",
                        isOptionalParam: hasInlineTags,
                        needsHoistedSingleGroup: true,
                        needsHoistedGroups: false,
                        inlineTagSetExpression: inlineExpr,
                        tagSetExpression: resolvedTagsLocal
                    );
                case FromWorldFieldKind.NativeComponentRead:
                case FromWorldFieldKind.NativeComponentWrite:
                    // Always requires a schedule param (EntityIndex, not TagSet).
                    // Inline tags are rejected by ScanFromWorldFields (TRECS082).
                    return new(
                        info.Kind,
                        info.FieldName,
                        t,
                        sym,
                        info.AspectData,
                        hasScheduleParam: true,
                        scheduleParamType: "EntityIndex",
                        scheduleParamName: lower + "Index",
                        isOptionalParam: false,
                        needsHoistedSingleGroup: false,
                        needsHoistedGroups: false,
                        inlineTagSetExpression: "",
                        tagSetExpression: ""
                    );
                case FromWorldFieldKind.NativeComponentLookupRead:
                case FromWorldFieldKind.NativeComponentLookupWrite:
                    return new(
                        info.Kind,
                        info.FieldName,
                        t,
                        sym,
                        info.AspectData,
                        hasScheduleParam: true,
                        scheduleParamType: hasInlineTags ? "TagSet?" : "TagSet",
                        scheduleParamName: lower + "Tags",
                        isOptionalParam: hasInlineTags,
                        needsHoistedSingleGroup: false,
                        needsHoistedGroups: true,
                        inlineTagSetExpression: inlineExpr,
                        tagSetExpression: resolvedTagsLocal
                    );
                case FromWorldFieldKind.NativeFactory:
                    // NativeFactory needs hoisted groups for creating lookups + dep tracking.
                    // GenericArgDisplay is "" (non-generic); the field type display is used instead.
                    return new(
                        info.Kind,
                        info.FieldName,
                        PerformanceCache.GetDisplayString(info.FieldType),
                        sym,
                        info.AspectData,
                        hasScheduleParam: true,
                        scheduleParamType: hasInlineTags ? "TagSet?" : "TagSet",
                        scheduleParamName: lower + "Tags",
                        isOptionalParam: hasInlineTags,
                        needsHoistedSingleGroup: false,
                        needsHoistedGroups: true,
                        inlineTagSetExpression: inlineExpr,
                        tagSetExpression: resolvedTagsLocal
                    );
                case FromWorldFieldKind.NativeEntitySetIndices:
                    return new(
                        info.Kind,
                        info.FieldName,
                        t,
                        sym,
                        info.AspectData,
                        hasScheduleParam: true,
                        scheduleParamType: hasInlineTags ? "TagSet?" : "TagSet",
                        scheduleParamName: lower + "Tags",
                        isOptionalParam: hasInlineTags,
                        needsHoistedSingleGroup: true,
                        needsHoistedGroups: false,
                        inlineTagSetExpression: inlineExpr,
                        tagSetExpression: resolvedTagsLocal
                    );
                case FromWorldFieldKind.NativeSetCommandBuffer:
                    // No schedule param: set type is on the field's generic arg, the writer
                    // is constructed unconditionally from the world.
                    return new(
                        info.Kind,
                        info.FieldName,
                        t,
                        sym,
                        info.AspectData,
                        hasScheduleParam: false,
                        scheduleParamType: "",
                        scheduleParamName: "",
                        isOptionalParam: false,
                        needsHoistedSingleGroup: false,
                        needsHoistedGroups: false,
                        inlineTagSetExpression: "",
                        tagSetExpression: ""
                    );
                case FromWorldFieldKind.NativeSetRead:
                    // No schedule param: set type is on the field's generic arg.
                    // Read-only lookup constructed from the set's entriesPerGroup.
                    return new(
                        info.Kind,
                        info.FieldName,
                        t,
                        sym,
                        info.AspectData,
                        hasScheduleParam: false,
                        scheduleParamType: "",
                        scheduleParamName: "",
                        isOptionalParam: false,
                        needsHoistedSingleGroup: false,
                        needsHoistedGroups: false,
                        inlineTagSetExpression: "",
                        tagSetExpression: ""
                    );
                case FromWorldFieldKind.NativeWorldAccessor:
                    // No schedule param, no groups, no deps, no tracking.
                    // Emits: _trecs_job.{field} = _trecs_world.ToNative();
                    return new(
                        info.Kind,
                        info.FieldName,
                        t,
                        sym,
                        info.AspectData,
                        hasScheduleParam: false,
                        scheduleParamType: "",
                        scheduleParamName: "",
                        isOptionalParam: false,
                        needsHoistedSingleGroup: false,
                        needsHoistedGroups: false,
                        inlineTagSetExpression: "",
                        tagSetExpression: ""
                    );
                case FromWorldFieldKind.GroupIndex:
                    // GroupIndex resolves to a single group via GetSingleGroupWithTags.
                    // With inline tags: optional TagSet? schedule parameter.
                    // Without inline tags: mandatory TagSet schedule parameter.
                    return new(
                        info.Kind,
                        info.FieldName,
                        t,
                        sym,
                        info.AspectData,
                        hasScheduleParam: true,
                        scheduleParamType: hasInlineTags ? "TagSet?" : "TagSet",
                        scheduleParamName: lower + "Tags",
                        isOptionalParam: hasInlineTags,
                        needsHoistedSingleGroup: true,
                        needsHoistedGroups: false,
                        inlineTagSetExpression: inlineExpr,
                        tagSetExpression: resolvedTagsLocal
                    );
                case FromWorldFieldKind.NativeEntityHandleBuffer:
                    // Resolves to a single group's entity handle buffer.
                    // Same tag resolution pattern as GroupIndex/ComponentBuffer.
                    return new(
                        info.Kind,
                        info.FieldName,
                        t,
                        sym,
                        info.AspectData,
                        hasScheduleParam: true,
                        scheduleParamType: hasInlineTags ? "TagSet?" : "TagSet",
                        scheduleParamName: lower + "Tags",
                        isOptionalParam: hasInlineTags,
                        needsHoistedSingleGroup: true,
                        needsHoistedGroups: false,
                        inlineTagSetExpression: inlineExpr,
                        tagSetExpression: resolvedTagsLocal
                    );
                default:
                    throw new System.ArgumentOutOfRangeException(nameof(info));
            }
        }

        internal static string LowerFirst(string s)
        {
            if (string.IsNullOrEmpty(s))
                return s;
            return char.ToLower(s[0]) + s.Substring(1);
        }
    }

    /// <summary>
    /// Classifies [FromWorld]-decorated field types into <see cref="FromWorldFieldKind"/>.
    /// </summary>
    internal static class FromWorldClassifier
    {
        public static FromWorldFieldKind Classify(INamedTypeSymbol typeSymbol)
        {
            // Source-gen-emitted NativeFactory nested struct inside an IAspect. These live
            // in the user's namespace (not Trecs), so check before the namespace gate.
            if (
                typeSymbol.Name == "NativeFactory"
                && typeSymbol.ContainingType != null
                && typeSymbol.ContainingType.AllInterfaces.Any(i => i.Name == "IAspect")
            )
            {
                return FromWorldFieldKind.NativeFactory;
            }

            // Namespace must be `Trecs`. Without this check, a user type with a colliding
            // name (e.g. `MyApp.NativeComponentBufferRead`) would be misclassified.
            var ns = PerformanceCache.GetDisplayString(typeSymbol.ContainingNamespace);
            if (ns != TrecsNamespaces.Trecs)
                return FromWorldFieldKind.Unsupported;

            // Non-generic types.
            if (!typeSymbol.IsGenericType)
            {
                return typeSymbol.Name switch
                {
                    "NativeWorldAccessor" => FromWorldFieldKind.NativeWorldAccessor,
                    "GroupIndex" => FromWorldFieldKind.GroupIndex,
                    "NativeEntityHandleBuffer" => FromWorldFieldKind.NativeEntityHandleBuffer,
                    _ => FromWorldFieldKind.Unsupported,
                };
            }

            // All remaining supported field types are generic on exactly one type parameter.
            if (typeSymbol.TypeArguments.Length != 1)
                return FromWorldFieldKind.Unsupported;

            return typeSymbol.Name switch
            {
                "NativeComponentBufferRead" => FromWorldFieldKind.NativeComponentBufferRead,
                "NativeComponentBufferWrite" => FromWorldFieldKind.NativeComponentBufferWrite,
                "NativeComponentRead" => FromWorldFieldKind.NativeComponentRead,
                "NativeComponentWrite" => FromWorldFieldKind.NativeComponentWrite,
                "NativeComponentLookupRead" => FromWorldFieldKind.NativeComponentLookupRead,
                "NativeComponentLookupWrite" => FromWorldFieldKind.NativeComponentLookupWrite,
                "NativeSetCommandBuffer" => FromWorldFieldKind.NativeSetCommandBuffer,
                "NativeEntitySetIndices" => FromWorldFieldKind.NativeEntitySetIndices,
                "NativeSetRead" => FromWorldFieldKind.NativeSetRead,
                _ => FromWorldFieldKind.Unsupported,
            };
        }
    }
}
