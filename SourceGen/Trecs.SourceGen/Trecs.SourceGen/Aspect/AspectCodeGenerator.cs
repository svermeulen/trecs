using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Trecs.SourceGen.Performance;
using Trecs.SourceGen.Shared;

namespace Trecs.SourceGen.Aspect
{
    /// <summary>
    /// Handles source code generation for Aspect implementations
    /// </summary>
    internal static class AspectCodeGenerator
    {
        /// <summary>
        /// Generates the complete source code for an Aspect
        /// </summary>
        public static string GenerateAspectSource(
            INamedTypeSymbol symbol,
            AspectAttributeData attributeData
        )
        {
            var componentCount = attributeData.ReadTypes.Length + attributeData.WriteTypes.Length;
            var sb = OptimizedStringBuilder.ForAspect(componentCount);
            var namespaceName = SymbolAnalyzer.GetNamespaceChain(symbol);
            var accessibility = SymbolAnalyzer.GetAccessibilityModifier(symbol);
            var containingTypes = SymbolAnalyzer.GetContainingTypeChain(symbol);

            // Generate using statements
            sb.AppendUsings(
                "System",
                "System.Runtime.CompilerServices",
                "Trecs.Collections",
                "Trecs",
                "Trecs.Internal"
            );

            // Generate in namespace if needed
            return sb.WrapInNamespace(
                    namespaceName,
                    (builder) =>
                    {
                        GenerateStructContent(
                            builder,
                            symbol,
                            attributeData,
                            accessibility,
                            containingTypes
                        );
                    }
                )
                .ToString();
        }

        /// <summary>
        /// Generates the struct content for Aspects
        /// </summary>
        private static void GenerateStructContent(
            OptimizedStringBuilder sb,
            INamedTypeSymbol symbol,
            AspectAttributeData attributeData,
            string accessibility,
            List<string> containingTypes
        )
        {
            int indentLevel = 0;

            // Open containing types
            foreach (var containingType in containingTypes)
            {
                sb.AppendLine(indentLevel, $"partial class {containingType}");
                sb.AppendLine(indentLevel, "{");
                indentLevel++;
            }

            // Start struct - user's partial already declares interfaces; C# merges partials
            var effectiveAccessibility = accessibility == "private" ? "internal" : accessibility;
            sb.WrapInType(
                effectiveAccessibility,
                "struct",
                symbol.Name,
                (builder) =>
                {
                    // Generate fields
                    GenerateFields(builder, indentLevel + 1, attributeData);

                    // Generate constructors
                    GenerateConstructors(builder, indentLevel + 1, symbol.Name, attributeData);

                    // DeclareDependencies removed — RuntimeJobScheduler handles deps implicitly

                    // Generate properties
                    GenerateProperties(builder, indentLevel + 1, attributeData);

                    // Generate helper methods
                    GenerateHelperMethods(builder, indentLevel + 1, symbol, attributeData);

                    // Generate nested NativeFactory struct for cross-entity access in jobs
                    GenerateNativeFactory(builder, indentLevel + 1, symbol, attributeData);
                },
                indentLevel
            );

            // Close containing types
            for (int i = containingTypes.Count - 1; i >= 0; i--)
            {
                indentLevel--;
                sb.AppendLine(indentLevel, "}");
            }
        }

        /// <summary>
        /// Emits a nested <c>NativeFactory</c> struct that bundles
        /// <c>NativeComponentLookupRead/Write&lt;T&gt;</c> fields for each component in the
        /// aspect. Provides a <c>Create(EntityIndex)</c> method that constructs a full
        /// aspect view for any entity. Declare as a <c>[FromWorld]</c> field on a Trecs
        /// job struct.
        /// </summary>
        private static void GenerateNativeFactory(
            OptimizedStringBuilder sb,
            int indentLevel,
            INamedTypeSymbol symbol,
            AspectAttributeData attributeData
        )
        {
            var allTypes = attributeData.AllComponentTypes;
            if (allTypes.Length == 0)
                return;

            var aspectName = symbol.Name;

            sb.AppendLine();
            sb.AppendLine(indentLevel, "/// <summary>");
            sb.AppendLine(
                indentLevel,
                $"/// Burst-compatible factory for constructing <see cref=\"{aspectName}\"/> views"
            );
            sb.AppendLine(
                indentLevel,
                "/// of cross-entity lookups inside a job. Declare as a <c>[FromWorld]</c> field."
            );
            sb.AppendLine(indentLevel, "/// </summary>");
            sb.AppendLine(
                indentLevel,
                "[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]"
            );
            sb.AppendLine(indentLevel, "public struct NativeFactory : System.IDisposable");
            sb.AppendLine(indentLevel, "{");

            // Fields — one lookup per component
            for (int i = 0; i < allTypes.Length; i++)
            {
                var componentTypeName = PerformanceCache.GetDisplayString(allTypes[i]);
                var isReadOnly = IsReadOnlyComponent(allTypes[i], attributeData);
                var lookupType = isReadOnly
                    ? $"NativeComponentLookupRead<{componentTypeName}>"
                    : $"NativeComponentLookupWrite<{componentTypeName}>";
                if (!isReadOnly)
                    sb.AppendLine(
                        indentLevel + 1,
                        "[Unity.Collections.NativeDisableParallelForRestriction]"
                    );
                sb.AppendLine(indentLevel + 1, $"{lookupType} _lookup{i};");
            }

            sb.AppendLine();

            // Constructor
            sb.AppendLine(
                indentLevel + 1,
                "[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]"
            );
            {
                var ctorSig = new System.Text.StringBuilder("public NativeFactory(");
                for (int i = 0; i < allTypes.Length; i++)
                {
                    var componentTypeName = PerformanceCache.GetDisplayString(allTypes[i]);
                    var isReadOnly = IsReadOnlyComponent(allTypes[i], attributeData);
                    var lookupType = isReadOnly
                        ? $"NativeComponentLookupRead<{componentTypeName}>"
                        : $"NativeComponentLookupWrite<{componentTypeName}>";
                    if (i > 0)
                        ctorSig.Append(", ");
                    ctorSig.Append($"{lookupType} lookup{i}");
                }
                ctorSig.Append(")");
                sb.AppendLine(indentLevel + 1, ctorSig.ToString());
            }
            sb.AppendLine(indentLevel + 1, "{");
            for (int i = 0; i < allTypes.Length; i++)
            {
                sb.AppendLine(indentLevel + 2, $"_lookup{i} = lookup{i};");
            }
            sb.AppendLine(indentLevel + 1, "}");

            sb.AppendLine();

            // Create method
            sb.AppendLine(indentLevel + 1, $"public {aspectName} Create(EntityIndex entityIndex)");
            sb.AppendLine(indentLevel + 1, "{");
            sb.AppendLine(indentLevel + 2, $"return new {aspectName}(");
            sb.AppendLine(indentLevel + 3, "entityIndex,");
            for (int i = 0; i < allTypes.Length; i++)
            {
                var suffix = (i == allTypes.Length - 1) ? ");" : ",";
                sb.AppendLine(
                    indentLevel + 3,
                    $"Trecs.Internal.JobGenSchedulingExtensions.GetBufferForGroupForJob(_lookup{i}, entityIndex.GroupIndex){suffix}"
                );
            }
            sb.AppendLine(indentLevel + 1, "}");

            sb.AppendLine();

            // Dispose method
            sb.AppendLine(indentLevel + 1, "public void Dispose()");
            sb.AppendLine(indentLevel + 1, "{");
            for (int i = 0; i < allTypes.Length; i++)
            {
                sb.AppendLine(indentLevel + 2, $"_lookup{i}.Dispose();");
            }
            sb.AppendLine(indentLevel + 1, "}");

            sb.AppendLine(indentLevel, "}");
        }

        /// <summary>
        /// Generates private fields for standard Aspects
        /// </summary>
        private static void GenerateFields(
            OptimizedStringBuilder sb,
            int indentLevel,
            AspectAttributeData attributeData
        )
        {
            sb.AppendLine(indentLevel, "EntityIndex _entityIndex;");
            sb.AppendLine();

            // Generate buffer fields efficiently
            var allTypes = attributeData.AllComponentTypes;
            sb.AppendBlock(
                allTypes,
                type =>
                {
                    var fieldName = ComponentTypeHelper.GetPropertyName(type);
                    var camelCaseFieldName = ComponentTypeHelper.ToCamelCase(fieldName);
                    var bufferType = GetBufferTypeName(type, attributeData);
                    return $"readonly {bufferType} _{camelCaseFieldName}Buffer;";
                },
                indentLevel
            );

            sb.AppendLine();
        }

        /// <summary>
        /// Generates constructors for standard Aspects
        /// </summary>
        private static void GenerateConstructors(
            OptimizedStringBuilder sb,
            int indentLevel,
            string typeName,
            AspectAttributeData attributeData
        )
        {
            var allTypes = attributeData.AllComponentTypes;

            // Generate EntityIndex constructor (used by ForEachAspect, AspectJob, Single/TrySingle)
            var entityIndexConstructorParams = new List<string> { "EntityIndex entityIndex" };
            entityIndexConstructorParams.AddRange(
                allTypes.Select(componentType =>
                {
                    var fieldName = ComponentTypeHelper.GetPropertyName(componentType);
                    var camelCaseFieldName = ComponentTypeHelper.ToCamelCase(fieldName);
                    var bufferType = GetBufferTypeName(componentType, attributeData);
                    return $"in {bufferType} {camelCaseFieldName}Buffer";
                })
            );

            sb.AppendLine(indentLevel, $"public {typeName}(");
            sb.AppendParameterList(entityIndexConstructorParams, indentLevel);
            sb.AppendLine(0, ")");
            sb.AppendLine(indentLevel, "{");
            sb.AppendLine(indentLevel + 1, "_entityIndex = entityIndex;");

            // Generate field assignments efficiently
            sb.AppendBlock(
                allTypes,
                componentType =>
                {
                    var fieldName = ComponentTypeHelper.GetPropertyName(componentType);
                    var camelCaseFieldName = ComponentTypeHelper.ToCamelCase(fieldName);
                    return $"_{camelCaseFieldName}Buffer = {camelCaseFieldName}Buffer;";
                },
                indentLevel + 1
            );

            sb.AppendLine(indentLevel, "}");
            sb.AppendLine();

            // Generate GroupIndex-only constructor (used by ForEachAspect hoisted iteration)
            var groupConstructorParams = new List<string> { "GroupIndex group" };
            groupConstructorParams.AddRange(
                allTypes.Select(componentType =>
                {
                    var fieldName = ComponentTypeHelper.GetPropertyName(componentType);
                    var camelCaseFieldName = ComponentTypeHelper.ToCamelCase(fieldName);
                    var bufferType = GetBufferTypeName(componentType, attributeData);
                    return $"in {bufferType} {camelCaseFieldName}Buffer";
                })
            );

            sb.AppendLine(indentLevel, $"internal {typeName}(");
            sb.AppendParameterList(groupConstructorParams, indentLevel);
            sb.AppendLine(0, ")");
            sb.AppendLine(indentLevel, "{");
            sb.AppendLine(indentLevel + 1, "_entityIndex = new EntityIndex(0, group);");

            sb.AppendBlock(
                allTypes,
                componentType =>
                {
                    var fieldName = ComponentTypeHelper.GetPropertyName(componentType);
                    var camelCaseFieldName = ComponentTypeHelper.ToCamelCase(fieldName);
                    return $"_{camelCaseFieldName}Buffer = {camelCaseFieldName}Buffer;";
                },
                indentLevel + 1
            );

            sb.AppendLine(indentLevel, "}");
            sb.AppendLine();

            // Generate SetIndex method for hoisted iteration (avoids per-entity struct reconstruction)
            sb.AppendLine(indentLevel, "[MethodImpl(MethodImplOptions.AggressiveInlining)]");
            sb.AppendLine(indentLevel, "internal void SetIndex(int index)");
            sb.AppendLine(indentLevel, "{");
            sb.AppendLine(indentLevel + 1, "_entityIndex = _entityIndex.WithIndex(index);");
            sb.AppendLine(indentLevel, "}");
            sb.AppendLine();

            // Generate constructor that takes WorldAccessor and EntityIndex
            sb.AppendLine(
                indentLevel,
                $"public {typeName}(WorldAccessor world, EntityIndex entityIndex)"
            );
            sb.AppendLine(indentLevel, "{");
            sb.AppendLine(indentLevel + 1, "_entityIndex = entityIndex;");
            sb.AppendLine();
            sb.AppendLine(indentLevel + 1, "// Eagerly populate component buffers for performance");

            // Generate batched buffer population for all component types
            GenerateBatchedQueryComponents(
                sb,
                indentLevel + 1,
                allTypes,
                "_entityIndex.GroupIndex",
                "world",
                type => $"_{ComponentTypeHelper.GetCamelCasePropertyName(type)}Buffer",
                isFieldAssignment: true,
                attributeData
            );

            sb.AppendLine(indentLevel, "}");
            sb.AppendLine();

            // Generate constructor that takes WorldAccessor and EntityHandle (convenience overload)
            sb.AppendLine(
                indentLevel,
                $"public {typeName}(WorldAccessor world, EntityHandle entityHandle)"
            );
            sb.AppendLine(indentLevel + 1, ": this(world, entityHandle.ToIndex(world))");
            sb.AppendLine(indentLevel, "{");
            sb.AppendLine(indentLevel, "}");
            sb.AppendLine();
        }

        /// <summary>
        /// Generates component access properties.
        /// </summary>
        private static void GenerateProperties(
            OptimizedStringBuilder sb,
            int indentLevel,
            AspectAttributeData attributeData
        )
        {
            // Generate read properties
            foreach (var readType in attributeData.ReadTypes)
            {
                var propertyName = ComponentTypeHelper.GetPropertyName(readType);
                var returnType = ComponentTypeHelper.GetPropertyReturnType(readType, true);
                var camelCaseFieldName = ComponentTypeHelper.ToCamelCase(propertyName);
                var accessExpression = ComponentTypeHelper.GetPropertyAccessExpression(
                    $"_{camelCaseFieldName}Buffer",
                    "_entityIndex.Index",
                    readType,
                    true
                );

                sb.AppendProperty(
                    returnType,
                    propertyName,
                    $"ref {accessExpression}",
                    indentLevel,
                    isInlined: true
                );
                sb.AppendLine();
            }

            // Generate write properties
            foreach (var writeType in attributeData.WriteTypes)
            {
                var propertyName = ComponentTypeHelper.GetPropertyName(writeType);
                var returnType = ComponentTypeHelper.GetPropertyReturnType(writeType, false);
                var (finalType, wasUnwrapped) = ComponentTypeHelper.UnwrapComponent(writeType);
                var camelCaseFieldName = ComponentTypeHelper.ToCamelCase(propertyName);
                var bufferName = $"_{camelCaseFieldName}Buffer";

                var accessExpression = GetWritePropertyAccessExpression(bufferName, writeType);

                sb.AppendProperty(
                    returnType,
                    propertyName,
                    $"ref {accessExpression}",
                    indentLevel,
                    isInlined: true
                );
                sb.AppendLine();
            }
        }

        /// <summary>
        /// Generates helper methods (EntityIndex, Index, Single, TrySingle, Query)
        /// </summary>
        private static void GenerateHelperMethods(
            OptimizedStringBuilder sb,
            int indentLevel,
            INamedTypeSymbol symbol,
            AspectAttributeData attributeData
        )
        {
            var typeName = symbol.Name;

            // Generate AspectQuery builder + unified Enumerator (replaces old
            // standalone Single/TrySingle/Query methods with a fluent builder API)
            GenerateAspectQueryAndEnumerator(sb, indentLevel, typeName, attributeData);

            // EntityIndex property
            sb.AppendProperty(
                "EntityIndex",
                "EntityIndex",
                "_entityIndex",
                indentLevel,
                isInlined: true
            );
            sb.AppendLine();

            // Index property (read-only)
            sb.AppendProperty("int", "Index", "_entityIndex.Index", indentLevel, isInlined: true);
            sb.AppendLine();

            // Entity operation methods (Remove, MoveTo)
            GenerateEntityOperationMethods(sb, indentLevel);
        }

        /// <summary>
        /// Generates Remove and MoveTo convenience methods that forward to
        /// WorldAccessor / NativeWorldAccessor.
        /// </summary>
        private static void GenerateEntityOperationMethods(
            OptimizedStringBuilder sb,
            int indentLevel
        )
        {
            var accessorSpecs = new[]
            {
                (Type: "WorldAccessor", Prefix: ""),
                (Type: "NativeWorldAccessor", Prefix: "in "),
            };

            foreach (var (accessorType, paramPrefix) in accessorSpecs)
            {
                // Remove and MoveTo(TagSet) are defined as extension methods in AspectExtensions.cs

                // MoveTo<T1..T4> generic overloads
                // (Can't be extension methods due to C# partial type inference limitation)
                for (int arity = 1; arity <= 4; arity++)
                {
                    var typeParams = string.Join(
                        ", ",
                        Enumerable.Range(1, arity).Select(i => $"T{i}")
                    );
                    var whereClause = string.Join(
                        " ",
                        Enumerable.Range(1, arity).Select(i => $"where T{i} : struct, ITag")
                    );

                    sb.AppendLine(
                        indentLevel,
                        "[MethodImpl(MethodImplOptions.AggressiveInlining)]"
                    );
                    sb.AppendLine(
                        indentLevel,
                        $"public readonly void MoveTo<{typeParams}>({paramPrefix}{accessorType} world) {whereClause} => world.MoveTo<{typeParams}>(_entityIndex);"
                    );
                    sb.AppendLine();
                }

                // AddToSet / RemoveFromSet
                // (Can't be extension methods due to C# partial type inference limitation)
                sb.AppendLine(indentLevel, "[MethodImpl(MethodImplOptions.AggressiveInlining)]");
                sb.AppendLine(
                    indentLevel,
                    $"public readonly void AddToSet<TSet>({paramPrefix}{accessorType} world) where TSet : struct, IEntitySet => world.SetAdd<TSet>(_entityIndex);"
                );
                sb.AppendLine();

                sb.AppendLine(indentLevel, "[MethodImpl(MethodImplOptions.AggressiveInlining)]");
                sb.AppendLine(
                    indentLevel,
                    $"public readonly void RemoveFromSet<TSet>({paramPrefix}{accessorType} world) where TSet : struct, IEntitySet => world.SetRemove<TSet>(_entityIndex);"
                );
                sb.AppendLine();
            }
        }

        /// <summary>
        /// Generates the <c>Query(WorldAccessor)</c> entry point, the nested <c>AspectQuery</c>
        /// fluent builder ref struct, and the unified <c>Enumerator</c> ref struct that handles
        /// both dense and sparse (set-filtered) iteration.
        ///
        /// Replaces the old standalone Single/TrySingle/Query static methods and the separate
        /// QueryEnumerable/SparseQueryEnumerable/QueryEnumerator/SparseQueryEnumerator types.
        /// </summary>
        private static void GenerateAspectQueryAndEnumerator(
            OptimizedStringBuilder sb,
            int indentLevel,
            string typeName,
            AspectAttributeData attributeData
        )
        {
            var allTypes = attributeData.AllComponentTypes;

            // --- Query(WorldAccessor) static entry point ---
            sb.AppendLine(indentLevel, "public static AspectQuery Query(WorldAccessor world)");
            sb.AppendLine(indentLevel + 1, "=> new AspectQuery(world.Query());");
            sb.AppendLine();

            // =====================================================================
            // AspectQuery ref struct — fluent builder with filter + terminal methods
            // =====================================================================
            sb.AppendLine(indentLevel, "public ref struct AspectQuery");
            sb.AppendLine(indentLevel, "{");

            // --- Fields ---
            sb.AppendLine(indentLevel + 1, "QueryBuilder _builder;");
            sb.AppendLine(indentLevel + 1, "SetId _set;");
            sb.AppendLine(indentLevel + 1, "bool _hasSet;");
            sb.AppendLine();

            // --- Constructor ---
            sb.AppendLine(indentLevel + 1, "internal AspectQuery(QueryBuilder builder)");
            sb.AppendLine(indentLevel + 1, "{");
            sb.AppendLine(indentLevel + 2, "_builder = builder;");
            sb.AppendLine(indentLevel + 2, "_set = default;");
            sb.AppendLine(indentLevel + 2, "_hasSet = false;");
            sb.AppendLine(indentLevel + 1, "}");
            sb.AppendLine();

            // --- WithTags filter methods ---
            EmitAspectQueryForwardingMethod(
                sb,
                indentLevel + 1,
                "WithTags",
                "struct, ITag",
                1,
                4,
                "WithTags"
            );
            EmitAspectQueryForwardingMethod(
                sb,
                indentLevel + 1,
                "WithTags",
                null,
                0,
                0,
                "WithTags",
                "TagSet tags"
            );
            // --- WithoutTags filter methods ---
            EmitAspectQueryForwardingMethod(
                sb,
                indentLevel + 1,
                "WithoutTags",
                "struct, ITag",
                1,
                4,
                "WithoutTags"
            );
            EmitAspectQueryForwardingMethod(
                sb,
                indentLevel + 1,
                "WithoutTags",
                null,
                0,
                0,
                "WithoutTags",
                "TagSet tags"
            );

            // --- InSet methods ---
            sb.AppendLine(
                indentLevel + 1,
                "public AspectQuery InSet<T>() where T : struct, IEntitySet"
            );
            sb.AppendLine(indentLevel + 1, "{");
            sb.AppendLine(
                indentLevel + 2,
                "Assert.That(!_hasSet, \"Only one set per query is supported.\");"
            );
            sb.AppendLine(indentLevel + 2, "_set = EntitySet<T>.Value.Id;");
            sb.AppendLine(indentLevel + 2, "_hasSet = true;");
            sb.AppendLine(indentLevel + 2, "return this;");
            sb.AppendLine(indentLevel + 1, "}");
            sb.AppendLine();

            sb.AppendLine(indentLevel + 1, "public AspectQuery InSet(SetDef setDef)");
            sb.AppendLine(indentLevel + 1, "{");
            sb.AppendLine(
                indentLevel + 2,
                "Assert.That(!_hasSet, \"Only one set per query is supported.\");"
            );
            sb.AppendLine(indentLevel + 2, "_set = setDef.Id;");
            sb.AppendLine(indentLevel + 2, "_hasSet = true;");
            sb.AppendLine(indentLevel + 2, "return this;");
            sb.AppendLine(indentLevel + 1, "}");
            sb.AppendLine();

            sb.AppendLine(indentLevel + 1, "public AspectQuery InSet(SetId setId)");
            sb.AppendLine(indentLevel + 1, "{");
            sb.AppendLine(
                indentLevel + 2,
                "Assert.That(!_hasSet, \"Only one set per query is supported.\");"
            );
            sb.AppendLine(indentLevel + 2, "_set = setId;");
            sb.AppendLine(indentLevel + 2, "_hasSet = true;");
            sb.AppendLine(indentLevel + 2, "return this;");
            sb.AppendLine(indentLevel + 1, "}");
            sb.AppendLine();

            // --- MatchByComponents() — adds WithComponents for every component the aspect declares ---
            sb.AppendLine(indentLevel + 1, "public AspectQuery MatchByComponents()");
            sb.AppendLine(indentLevel + 1, "{");
            foreach (var compType in allTypes)
                sb.AppendLine(
                    indentLevel + 2,
                    $"_builder = _builder.WithComponents<{PerformanceCache.GetDisplayString(compType)}>();"
                );
            sb.AppendLine(indentLevel + 2, "return this;");
            sb.AppendLine(indentLevel + 1, "}");
            sb.AppendLine();

            // --- Single() terminal ---
            sb.AppendLine(indentLevel + 1, $"public {typeName} Single()");
            sb.AppendLine(indentLevel + 1, "{");
            sb.AppendLine(
                indentLevel + 2,
                $"Assert.That(_builder.HasAnyCriteria || _hasSet, \"{typeName}.Query requires at least one constraint before calling Single().\");"
            );
            sb.AppendLine(indentLevel + 2, "EntityIndex __ei;");
            sb.AppendLine(indentLevel + 2, "if (_hasSet)");
            sb.AppendLine(indentLevel + 2, "{");
            sb.AppendLine(indentLevel + 3, "var __sb = _builder.InSet(_set);");
            sb.AppendLine(indentLevel + 3, "__ei = __sb.SingleEntityIndex();");
            sb.AppendLine(indentLevel + 2, "}");
            sb.AppendLine(indentLevel + 2, "else");
            sb.AppendLine(indentLevel + 2, "{");
            sb.AppendLine(indentLevel + 3, "__ei = _builder.SingleEntityIndex();");
            sb.AppendLine(indentLevel + 2, "}");
            sb.AppendLine(indentLevel + 2, "var __world = _builder.World;");

            GenerateBatchedQueryComponents(
                sb,
                indentLevel + 2,
                allTypes,
                "__ei.GroupIndex",
                "__world",
                type => $"{ComponentTypeHelper.GetCamelCasePropertyName(type)}Buffer",
                isFieldAssignment: false,
                attributeData
            );

            var ctorArgs = new List<string> { "__ei" };
            ctorArgs.AddRange(
                allTypes.Select(t =>
                {
                    var n = ComponentTypeHelper.GetPropertyName(t);
                    return $"{ComponentTypeHelper.ToCamelCase(n)}Buffer";
                })
            );
            sb.AppendLine(indentLevel + 2, "return");
            sb.AppendConstructorCall(typeName, ctorArgs, indentLevel + 3);
            sb.AppendLine(0, ";");
            sb.AppendLine(indentLevel + 1, "}");
            sb.AppendLine();

            // --- TrySingle() terminal ---
            sb.AppendLine(indentLevel + 1, $"public bool TrySingle(out {typeName} view)");
            sb.AppendLine(indentLevel + 1, "{");
            sb.AppendLine(
                indentLevel + 2,
                $"Assert.That(_builder.HasAnyCriteria || _hasSet, \"{typeName}.Query requires at least one constraint before calling TrySingle().\");"
            );
            sb.AppendLine(indentLevel + 2, "bool found;");
            sb.AppendLine(indentLevel + 2, "EntityIndex __ei;");
            sb.AppendLine(indentLevel + 2, "if (_hasSet)");
            sb.AppendLine(indentLevel + 2, "{");
            sb.AppendLine(indentLevel + 3, "var __sb = _builder.InSet(_set);");
            sb.AppendLine(indentLevel + 3, "found = __sb.TrySingleEntityIndex(out __ei);");
            sb.AppendLine(indentLevel + 2, "}");
            sb.AppendLine(indentLevel + 2, "else");
            sb.AppendLine(indentLevel + 2, "{");
            sb.AppendLine(indentLevel + 3, "found = _builder.TrySingleEntityIndex(out __ei);");
            sb.AppendLine(indentLevel + 2, "}");
            sb.AppendLine(indentLevel + 2, "if (!found) { view = default; return false; }");
            sb.AppendLine(indentLevel + 2, "var __world = _builder.World;");

            GenerateBatchedQueryComponents(
                sb,
                indentLevel + 2,
                allTypes,
                "__ei.GroupIndex",
                "__world",
                type => $"{ComponentTypeHelper.GetCamelCasePropertyName(type)}Buffer",
                isFieldAssignment: false,
                attributeData
            );

            sb.AppendLine(indentLevel + 2, "view =");
            sb.AppendConstructorCall(typeName, ctorArgs, indentLevel + 3);
            sb.AppendLine(0, ";");
            sb.AppendLine(indentLevel + 2, "return true;");
            sb.AppendLine(indentLevel + 1, "}");
            sb.AppendLine();

            // --- Count() terminal ---
            sb.AppendLine(indentLevel + 1, "public int Count()");
            sb.AppendLine(indentLevel + 1, "{");
            sb.AppendLine(
                indentLevel + 2,
                $"Assert.That(_builder.HasAnyCriteria || _hasSet, \"{typeName}.Query requires at least one constraint before calling Count().\");"
            );
            sb.AppendLine(indentLevel + 2, "if (_hasSet)");
            sb.AppendLine(indentLevel + 2, "{");
            sb.AppendLine(indentLevel + 3, "var __sb = _builder.InSet(_set);");
            sb.AppendLine(indentLevel + 3, "return __sb.Count();");
            sb.AppendLine(indentLevel + 2, "}");
            sb.AppendLine(indentLevel + 2, "return _builder.Count();");
            sb.AppendLine(indentLevel + 1, "}");
            sb.AppendLine();

            // --- GetEnumerator() terminal ---
            sb.AppendLine(indentLevel + 1, "public Enumerator GetEnumerator()");
            sb.AppendLine(indentLevel + 1, "{");
            sb.AppendLine(
                indentLevel + 2,
                $"Assert.That(_builder.HasAnyCriteria || _hasSet, \"{typeName}.Query requires at least one constraint before iterating.\");"
            );
            sb.AppendLine(indentLevel + 2, "if (_hasSet)");
            sb.AppendLine(indentLevel + 3, "return new Enumerator(_builder.InSet(_set));");
            sb.AppendLine(indentLevel + 2, "return new Enumerator(_builder);");
            sb.AppendLine(indentLevel + 1, "}");

            sb.AppendLine(indentLevel, "}"); // close AspectQuery
            sb.AppendLine();

            // =====================================================================
            // Enumerator ref struct — unified dense + sparse iteration
            // =====================================================================
            sb.AppendLine(indentLevel, "public ref struct Enumerator");
            sb.AppendLine(indentLevel, "{");

            // Fields
            sb.AppendLine(indentLevel + 1, "readonly WorldAccessor _world;");
            sb.AppendLine(indentLevel + 1, "readonly bool _isSparse;");
            sb.AppendLine();
            sb.AppendLine(indentLevel + 1, "// Dense state");
            sb.AppendLine(indentLevel + 1, "DenseGroupSliceIterator _denseGroupIter;");
            sb.AppendLine(indentLevel + 1, "int _denseSlicePosition;");
            sb.AppendLine(indentLevel + 1, "int _denseSliceCount;");
            sb.AppendLine();
            sb.AppendLine(indentLevel + 1, "// Sparse state");
            sb.AppendLine(indentLevel + 1, "SparseGroupSliceIterator _sparseGroupIter;");
            sb.AppendLine(indentLevel + 1, "EntitySetIndices _indices;");
            sb.AppendLine(indentLevel + 1, "int _sparseCount;");
            sb.AppendLine(indentLevel + 1, "int _sparsePosition;");
            sb.AppendLine();
            sb.AppendLine(indentLevel + 1, $"{typeName} _aspect;");
            sb.AppendLine();

            // Dense constructor
            sb.AppendLine(indentLevel + 1, "internal Enumerator(QueryBuilder builder)");
            sb.AppendLine(indentLevel + 1, "{");
            sb.AppendLine(indentLevel + 2, "_world = builder.World;");
            sb.AppendLine(indentLevel + 2, "_isSparse = false;");
            sb.AppendLine(indentLevel + 2, "_denseGroupIter = builder.GroupSlices();");
            sb.AppendLine(indentLevel + 2, "_denseSlicePosition = 0;");
            sb.AppendLine(indentLevel + 2, "_denseSliceCount = 0;");
            sb.AppendLine(indentLevel + 2, "_sparseGroupIter = default;");
            sb.AppendLine(indentLevel + 2, "_indices = default;");
            sb.AppendLine(indentLevel + 2, "_sparseCount = 0;");
            sb.AppendLine(indentLevel + 2, "_sparsePosition = -1;");
            sb.AppendLine(indentLevel + 2, "_aspect = default;");
            sb.AppendLine(indentLevel + 1, "}");
            sb.AppendLine();

            // Sparse constructor
            sb.AppendLine(indentLevel + 1, "internal Enumerator(SparseQueryBuilder builder)");
            sb.AppendLine(indentLevel + 1, "{");
            sb.AppendLine(indentLevel + 2, "_world = builder.World;");
            sb.AppendLine(indentLevel + 2, "_isSparse = true;");
            sb.AppendLine(indentLevel + 2, "_sparseGroupIter = builder.GroupSlices();");
            sb.AppendLine(indentLevel + 2, "_indices = default;");
            sb.AppendLine(indentLevel + 2, "_sparseCount = 0;");
            sb.AppendLine(indentLevel + 2, "_sparsePosition = -1;");
            sb.AppendLine(indentLevel + 2, "_denseGroupIter = default;");
            sb.AppendLine(indentLevel + 2, "_denseSlicePosition = 0;");
            sb.AppendLine(indentLevel + 2, "_denseSliceCount = 0;");
            sb.AppendLine(indentLevel + 2, "_aspect = default;");
            sb.AppendLine(indentLevel + 1, "}");
            sb.AppendLine();

            // MoveNext dispatch
            sb.AppendLine(indentLevel + 1, "[MethodImpl(MethodImplOptions.AggressiveInlining)]");
            sb.AppendLine(indentLevel + 1, "public bool MoveNext()");
            sb.AppendLine(indentLevel + 1, "{");
            sb.AppendLine(indentLevel + 2, "if (_isSparse) return MoveNextSparse();");
            sb.AppendLine(indentLevel + 2, "return MoveNextDense();");
            sb.AppendLine(indentLevel + 1, "}");
            sb.AppendLine();

            // --- MoveNextDense ---
            sb.AppendLine(indentLevel + 1, "[MethodImpl(MethodImplOptions.AggressiveInlining)]");
            sb.AppendLine(indentLevel + 1, "bool MoveNextDense()");
            sb.AppendLine(indentLevel + 1, "{");
            sb.AppendLine(indentLevel + 2, "var pos = _denseSlicePosition;");
            sb.AppendLine(indentLevel + 2, "if (pos < _denseSliceCount)");
            sb.AppendLine(indentLevel + 2, "{");
            sb.AppendLine(indentLevel + 3, "_denseSlicePosition = pos + 1;");
            sb.AppendLine(indentLevel + 3, "_aspect.SetIndex(pos);");
            sb.AppendLine(indentLevel + 3, "return true;");
            sb.AppendLine(indentLevel + 2, "}");
            sb.AppendLine(indentLevel + 2, "return AdvanceToNextDenseSlice();");
            sb.AppendLine(indentLevel + 1, "}");
            sb.AppendLine();

            // --- AdvanceToNextDenseSlice ---
            sb.AppendLine(indentLevel + 1, "bool AdvanceToNextDenseSlice()");
            sb.AppendLine(indentLevel + 1, "{");
            sb.AppendLine(indentLevel + 2, "if (!_denseGroupIter.MoveNext())");
            sb.AppendLine(indentLevel + 3, "return false;");
            sb.AppendLine();
            sb.AppendLine(indentLevel + 2, "var slice = _denseGroupIter.Current;");
            sb.AppendLine(indentLevel + 2, "var __group = slice.GroupIndex;");
            sb.AppendLine(indentLevel + 2, "_denseSliceCount = slice.Count;");
            sb.AppendLine();

            foreach (var type in allTypes)
            {
                var fieldName = ComponentTypeHelper.GetPropertyName(type);
                var varName = ComponentTypeHelper.ToCamelCase(fieldName) + "Buffer";
                var typeDisplay = PerformanceCache.GetDisplayString(type);
                bool isReadOnly = IsReadOnlyComponent(type, attributeData);
                var bufferSuffix = isReadOnly ? "Read" : "Write";
                sb.AppendLine(
                    indentLevel + 2,
                    $"var {varName} = _world.ComponentBuffer<{typeDisplay}>(__group).{bufferSuffix};"
                );
            }
            sb.AppendLine();

            var groupCtorArgs = new List<string> { "__group" };
            groupCtorArgs.AddRange(
                allTypes.Select(t =>
                {
                    var n = ComponentTypeHelper.GetPropertyName(t);
                    return $"{ComponentTypeHelper.ToCamelCase(n)}Buffer";
                })
            );
            sb.AppendLine(
                indentLevel + 2,
                $"_aspect = new {typeName}({string.Join(", ", groupCtorArgs)});"
            );
            sb.AppendLine(indentLevel + 2, "_denseSlicePosition = 1;");
            sb.AppendLine(indentLevel + 2, "return true;");
            sb.AppendLine(indentLevel + 1, "}");
            sb.AppendLine();

            // --- MoveNextSparse ---
            sb.AppendLine(indentLevel + 1, "[MethodImpl(MethodImplOptions.AggressiveInlining)]");
            sb.AppendLine(indentLevel + 1, "bool MoveNextSparse()");
            sb.AppendLine(indentLevel + 1, "{");
            sb.AppendLine(indentLevel + 2, "var pos = _sparsePosition + 1;");
            sb.AppendLine(indentLevel + 2, "if (pos < _sparseCount)");
            sb.AppendLine(indentLevel + 2, "{");
            sb.AppendLine(indentLevel + 3, "var idx = _indices[pos];");
            sb.AppendLine(indentLevel + 3, "_sparsePosition = pos;");
            sb.AppendLine(indentLevel + 3, "_aspect.SetIndex(idx);");
            sb.AppendLine(indentLevel + 3, "return true;");
            sb.AppendLine(indentLevel + 2, "}");
            sb.AppendLine(indentLevel + 2, "return AdvanceToNextSparseSlice();");
            sb.AppendLine(indentLevel + 1, "}");
            sb.AppendLine();

            // --- AdvanceToNextSparseSlice ---
            sb.AppendLine(indentLevel + 1, "bool AdvanceToNextSparseSlice()");
            sb.AppendLine(indentLevel + 1, "{");
            sb.AppendLine(indentLevel + 2, "while (_sparseGroupIter.MoveNext())");
            sb.AppendLine(indentLevel + 2, "{");
            sb.AppendLine(indentLevel + 3, "var slice = _sparseGroupIter.Current;");
            sb.AppendLine(indentLevel + 3, "_indices = slice.Indices;");
            sb.AppendLine(indentLevel + 3, "_sparseCount = slice.Indices.Count;");
            sb.AppendLine(indentLevel + 3, "_sparsePosition = -1;");
            sb.AppendLine();
            sb.AppendLine(indentLevel + 3, "if (_sparseCount > 0)");
            sb.AppendLine(indentLevel + 3, "{");
            sb.AppendLine(indentLevel + 4, "var idx = _indices[0];");
            sb.AppendLine(indentLevel + 4, "var __group = slice.GroupIndex;");

            foreach (var type in allTypes)
            {
                var fieldName = ComponentTypeHelper.GetPropertyName(type);
                var varName = ComponentTypeHelper.ToCamelCase(fieldName) + "Buffer";
                var typeDisplay = PerformanceCache.GetDisplayString(type);
                bool isReadOnly = IsReadOnlyComponent(type, attributeData);
                var bufferSuffix = isReadOnly ? "Read" : "Write";
                sb.AppendLine(
                    indentLevel + 4,
                    $"var {varName} = _world.ComponentBuffer<{typeDisplay}>(__group).{bufferSuffix};"
                );
            }

            sb.AppendLine(
                indentLevel + 4,
                $"_aspect = new {typeName}({string.Join(", ", groupCtorArgs)});"
            );
            sb.AppendLine(indentLevel + 4, "_aspect.SetIndex(idx);");
            sb.AppendLine(indentLevel + 4, "_sparsePosition = 0;");
            sb.AppendLine(indentLevel + 4, "return true;");
            sb.AppendLine(indentLevel + 3, "}");
            sb.AppendLine(indentLevel + 2, "}");
            sb.AppendLine(indentLevel + 2, "return false;");
            sb.AppendLine(indentLevel + 1, "}");
            sb.AppendLine();

            // Current
            sb.AppendLine(indentLevel + 1, $"public {typeName} Current");
            sb.AppendLine(indentLevel + 1, "{");
            sb.AppendLine(indentLevel + 2, "[MethodImpl(MethodImplOptions.AggressiveInlining)]");
            sb.AppendLine(indentLevel + 2, "get => _aspect;");
            sb.AppendLine(indentLevel + 1, "}");

            sb.AppendLine(indentLevel, "}"); // close Enumerator
            sb.AppendLine();
        }

        /// <summary>
        /// Emits generic forwarding methods on the AspectQuery for WithTags/WithoutTags.
        /// For arityMin..arityMax, emits generic overloads. When arityMin==0 and a paramStr
        /// is given, emits a non-generic overload (e.g. WithTags(TagSet tags)).
        /// </summary>
        private static void EmitAspectQueryForwardingMethod(
            OptimizedStringBuilder sb,
            int indentLevel,
            string methodName,
            string? constraint,
            int arityMin,
            int arityMax,
            string innerMethodName,
            string? nonGenericParam = null
        )
        {
            if (nonGenericParam != null)
            {
                // Non-generic overload: e.g. WithTags(TagSet tags)
                var argName = nonGenericParam.Split(' ').Last();
                sb.AppendLine(indentLevel, $"public AspectQuery {methodName}({nonGenericParam})");
                sb.AppendLine(
                    indentLevel,
                    $"{{ _builder = _builder.{innerMethodName}({argName}); return this; }}"
                );
                sb.AppendLine();
                return;
            }

            for (int arity = arityMin; arity <= arityMax; arity++)
            {
                var typeParams = string.Join(", ", Enumerable.Range(1, arity).Select(i => $"T{i}"));
                var whereClause = string.Join(
                    " ",
                    Enumerable.Range(1, arity).Select(i => $"where T{i} : {constraint}")
                );
                sb.AppendLine(
                    indentLevel,
                    $"public AspectQuery {methodName}<{typeParams}>() {whereClause}"
                );
                sb.AppendLine(
                    indentLevel,
                    $"{{ _builder = _builder.{innerMethodName}<{typeParams}>(); return this; }}"
                );
                sb.AppendLine();
            }
        }

        /// <summary>
        /// Returns a C# expression that evaluates to a TagSet for the given tag types.
        /// Uses static TagSet&lt;T1,...&gt;.Value for 1-4 tags, runtime TagSet.FromTags for 5+.
        /// </summary>
        internal static string GenerateTagSetExpression(ImmutableArray<ITypeSymbol> tagTypes)
        {
            var tagTypeNames = tagTypes.Select(t => PerformanceCache.GetDisplayString(t));

            if (tagTypes.Length <= 4)
            {
                return $"TagSet<{string.Join(", ", tagTypeNames)}>.Value";
            }

            var tagExprs = tagTypeNames.Select(n => $"Tag<{n}>.Value");
            return $"TagSet.FromTags(new Tag[] {{ {string.Join(", ", tagExprs)} }})";
        }

        /// <summary>
        /// Generates the source code for an AspectInterface
        /// </summary>
        public static string GenerateAspectInterfaceSource(
            INamedTypeSymbol symbol,
            AspectInterfaceData attributeData
        )
        {
            var componentCount = attributeData.ReadTypes.Length + attributeData.WriteTypes.Length;
            var sb = OptimizedStringBuilder.ForAspect(componentCount);
            var namespaceName = SymbolAnalyzer.GetNamespaceChain(symbol);
            var accessibility = SymbolAnalyzer.GetAccessibilityModifier(symbol);
            var containingTypes = SymbolAnalyzer.GetContainingTypeChain(symbol);

            // Generate using statements
            sb.AppendUsings(
                "System",
                "System.Runtime.CompilerServices",
                "Trecs.Collections",
                "Trecs",
                "Trecs.Internal"
            );

            // Generate in namespace if needed
            return sb.WrapInNamespace(
                    namespaceName,
                    (builder) =>
                    {
                        GenerateInterfaceContent(
                            builder,
                            symbol,
                            attributeData,
                            accessibility,
                            containingTypes
                        );
                    }
                )
                .ToString();
        }

        /// <summary>
        /// Generates the interface content for AspectInterface
        /// </summary>
        private static void GenerateInterfaceContent(
            OptimizedStringBuilder sb,
            INamedTypeSymbol symbol,
            AspectInterfaceData attributeData,
            string accessibility,
            List<string> containingTypes
        )
        {
            int indentLevel = 0;

            // Open containing types
            foreach (var containingType in containingTypes)
            {
                sb.AppendLine(indentLevel, $"partial class {containingType}");
                sb.AppendLine(indentLevel, "{");
                indentLevel++;
            }

            // Start interface - user's partial already declares base interfaces; C# merges partials
            var effectiveAccessibility = accessibility == "private" ? "internal" : accessibility;

            sb.AppendLine(indentLevel, $"{effectiveAccessibility} partial interface {symbol.Name}");
            sb.AppendLine(indentLevel, "{");

            // Note: EntityIndex is inherited from Trecs.IAspect — re-declaring it here would
            // shadow the base and produce CS0108. The user's partial already lists IAspect in
            // the base list (that's how we detected this type as an aspect interface).

            // Generate read properties with ref readonly
            foreach (var readType in attributeData.ReadTypes)
            {
                var propertyName = ComponentTypeHelper.GetPropertyName(readType);
                var returnType = ComponentTypeHelper.GetPropertyReturnType(readType, true);

                sb.AppendLine(indentLevel + 1, $"{returnType} {propertyName} {{ get; }}");
            }

            // Generate write properties with ref (only getter needed, since ref provides both read and write)
            foreach (var writeType in attributeData.WriteTypes)
            {
                var propertyName = ComponentTypeHelper.GetPropertyName(writeType);
                var returnType = ComponentTypeHelper.GetPropertyReturnType(writeType, false);

                sb.AppendLine(indentLevel + 1, $"{returnType} {propertyName} {{ get; }}");
            }

            sb.AppendLine(indentLevel, "}");

            // Close containing types
            for (int i = containingTypes.Count - 1; i >= 0; i--)
            {
                indentLevel--;
                sb.AppendLine(indentLevel, "}");
            }
        }

        /// <summary>
        /// Generates individual GetBuffer().Read/Write calls per component.
        /// </summary>
        private static void GenerateBatchedQueryComponents(
            OptimizedStringBuilder sb,
            int indentLevel,
            ImmutableArray<ITypeSymbol> allTypes,
            string groupExpression,
            string worldExpression,
            Func<ITypeSymbol, string> getVarName,
            bool isFieldAssignment,
            AspectAttributeData attributeData
        )
        {
            foreach (var type in allTypes)
            {
                var varName = getVarName(type);
                var typeDisplay = PerformanceCache.GetDisplayString(type);
                bool isReadOnly = IsReadOnlyComponent(type, attributeData);
                var bufferSuffix = isReadOnly ? "Read" : "Write";
                var prefix = isFieldAssignment ? "" : "var ";
                sb.AppendLine(
                    indentLevel,
                    $"{prefix}{varName} = {worldExpression}.ComponentBuffer<{typeDisplay}>({groupExpression}).{bufferSuffix};"
                );
            }
        }

        /// <summary>
        /// Gets the access expression for write properties, handling nested single-value components
        /// </summary>
        private static string GetWritePropertyAccessExpression(
            string bufferName,
            ITypeSymbol componentType
        )
        {
            var (finalType, wasUnwrapped) = ComponentTypeHelper.UnwrapComponent(componentType);

            var baseExpression = $"{bufferName}[_entityIndex.Index]";

            if (wasUnwrapped)
            {
                // Build the full unwrapping chain for nested single-value components
                var expression = baseExpression;
                var currentType = componentType;

                while (
                    currentType is INamedTypeSymbol namedType
                    && ComponentTypeHelper.IsUnwrapComponent(namedType)
                )
                {
                    var field = ComponentTypeHelper.GetUnwrapComponentField(namedType);
                    if (field == null)
                        break;

                    expression += $".{field.Name}";
                    currentType = field.Type;
                }

                return expression;
            }

            return baseExpression;
        }

        private static bool IsReadOnlyComponent(ITypeSymbol type, AspectAttributeData attributeData)
        {
            bool isInRead = attributeData.ReadTypes.Any(r =>
                SymbolEqualityComparer.Default.Equals(r, type)
            );
            bool isInWrite = attributeData.WriteTypes.Any(w =>
                SymbolEqualityComparer.Default.Equals(w, type)
            );
            return isInRead && !isInWrite;
        }

        private static string GetBufferTypeName(ITypeSymbol type, AspectAttributeData attributeData)
        {
            return IsReadOnlyComponent(type, attributeData)
                ? $"NativeComponentBufferRead<{PerformanceCache.GetDisplayString(type)}>"
                : $"NativeComponentBufferWrite<{PerformanceCache.GetDisplayString(type)}>";
        }
    }
}
