using System.Collections.Generic;
using System.Linq;
using System.Text;
using Trecs.SourceGen.Shared;

namespace Trecs.SourceGen.Aspect
{
    /// <summary>
    /// Aspect codegen — takes a fully precomputed <see cref="AspectModel"/> and emits the
    /// generated partial. Touches zero Roslyn symbols: every string the emitter needs is
    /// already baked into the model by <see cref="AspectModelBuilder"/>. That keeps this
    /// stage a pure function of the equatable model, which is what lets the incremental
    /// generator cache hit when nothing observable about the aspect has changed.
    /// </summary>
    internal static class AspectCodeGenerator
    {
        public static string GenerateAspectSource(AspectModel model)
        {
            var sb = OptimizedStringBuilder.ForAspect(model.Components.All.Length);
            sb.AppendUsings(CommonUsings.WithExtras("System", "System.Runtime.CompilerServices"));

            return sb.WrapInNamespace(
                    model.Namespace,
                    (builder) => GenerateStructContent(builder, model)
                )
                .ToString();
        }

        public static string GenerateAspectInterfaceSource(AspectModel model)
        {
            var sb = OptimizedStringBuilder.ForAspect(
                model.Components.Read.Length + model.Components.Write.Length
            );
            sb.AppendUsings(CommonUsings.WithExtras("System", "System.Runtime.CompilerServices"));

            return sb.WrapInNamespace(
                    model.Namespace,
                    (builder) => GenerateInterfaceContent(builder, model)
                )
                .ToString();
        }

        // -------------------------------------------------------------------------------------
        // Struct path
        // -------------------------------------------------------------------------------------

        private static void GenerateStructContent(OptimizedStringBuilder sb, AspectModel model)
        {
            var effectiveAccessibility =
                model.Accessibility == "private" ? "internal" : model.Accessibility;

            sb.WrapInContainingTypes(
                model.ContainingTypes.ToArray(),
                0,
                (b, indentLevel) =>
                    b.WrapInType(
                        effectiveAccessibility,
                        "struct",
                        model.TypeName,
                        (builder) =>
                        {
                            GenerateFields(builder, indentLevel + 1, model.Components.All);
                            GenerateConstructors(
                                builder,
                                indentLevel + 1,
                                model.TypeName,
                                model.Components.All
                            );
                            GenerateProperties(builder, indentLevel + 1, model.Components);
                            GenerateHelperMethods(
                                builder,
                                indentLevel + 1,
                                model.TypeName,
                                model.Components.All
                            );
                            GenerateNativeFactory(
                                builder,
                                indentLevel + 1,
                                model.TypeName,
                                model.Components.All
                            );
                        },
                        indentLevel
                    )
            );
        }

        private static void GenerateFields(
            OptimizedStringBuilder sb,
            int indentLevel,
            EquatableArray<ComponentModel> allComponents
        )
        {
            sb.AppendLine(indentLevel, GeneratedCodeAttributes.Line);
            sb.AppendLine(indentLevel, "EntityIndex __entityIndex;");
            sb.AppendLine();

            foreach (var c in allComponents)
            {
                sb.AppendLine(indentLevel, GeneratedCodeAttributes.Line);
                sb.AppendLine(indentLevel, $"readonly {c.BufferTypeName} {c.BufferFieldName};");
            }

            sb.AppendLine();
        }

        private static void GenerateConstructors(
            OptimizedStringBuilder sb,
            int indentLevel,
            string typeName,
            EquatableArray<ComponentModel> allComponents
        )
        {
            // EntityIndex constructor (used by aspect-mode [ForEachEntity], AspectJob, Single/TrySingle)
            var entityIndexParams = new List<string> { "EntityIndex entityIndex" };
            foreach (var c in allComponents)
                entityIndexParams.Add($"in {c.BufferTypeName} {c.BufferParamName}");

            sb.AppendLine(indentLevel, GeneratedCodeAttributes.Line);
            sb.AppendLine(indentLevel, $"public {typeName}(");
            sb.AppendParameterList(entityIndexParams, indentLevel);
            sb.AppendLine(0, ")");
            sb.AppendLine(indentLevel, "{");
            sb.AppendLine(indentLevel + 1, "__entityIndex = entityIndex;");
            sb.AppendBlock(
                allComponents,
                c => $"{c.BufferFieldName} = {c.BufferParamName};",
                indentLevel + 1
            );
            sb.AppendLine(indentLevel, "}");
            sb.AppendLine();

            // GroupIndex-only constructor (used by aspect-mode [ForEachEntity] hoisted iteration)
            var groupParams = new List<string> { "GroupIndex group" };
            foreach (var c in allComponents)
                groupParams.Add($"in {c.BufferTypeName} {c.BufferParamName}");

            sb.AppendLine(indentLevel, GeneratedCodeAttributes.Line);
            sb.AppendLine(indentLevel, $"internal {typeName}(");
            sb.AppendParameterList(groupParams, indentLevel);
            sb.AppendLine(0, ")");
            sb.AppendLine(indentLevel, "{");
            sb.AppendLine(indentLevel + 1, "__entityIndex = new EntityIndex(0, group);");
            sb.AppendBlock(
                allComponents,
                c => $"{c.BufferFieldName} = {c.BufferParamName};",
                indentLevel + 1
            );
            sb.AppendLine(indentLevel, "}");
            sb.AppendLine();

            // SetIndex helper for hoisted iteration (avoids per-entity struct reconstruction)
            sb.AppendLine(indentLevel, "[MethodImpl(MethodImplOptions.AggressiveInlining)]");
            sb.AppendLine(indentLevel, GeneratedCodeAttributes.Line);
            sb.AppendLine(indentLevel, "internal void SetIndex(int index)");
            sb.AppendLine(indentLevel, "{");
            sb.AppendLine(indentLevel + 1, "__entityIndex = __entityIndex.WithIndex(index);");
            sb.AppendLine(indentLevel, "}");
            sb.AppendLine();

            // WorldAccessor + EntityIndex constructor
            sb.AppendLine(indentLevel, GeneratedCodeAttributes.Line);
            sb.AppendLine(
                indentLevel,
                $"public {typeName}(WorldAccessor world, EntityIndex entityIndex)"
            );
            sb.AppendLine(indentLevel, "{");
            sb.AppendLine(indentLevel + 1, "__entityIndex = entityIndex;");
            sb.AppendLine();
            sb.AppendLine(indentLevel + 1, "// Eagerly populate component buffers for performance");
            EmitBufferAssignments(
                sb,
                indentLevel + 1,
                allComponents,
                groupExpression: "__entityIndex.GroupIndex",
                worldExpression: "world",
                lhs: c => c.BufferFieldName,
                isFieldAssignment: true
            );
            sb.AppendLine(indentLevel, "}");
            sb.AppendLine();

            // WorldAccessor + EntityHandle constructor (convenience overload)
            sb.AppendLine(indentLevel, GeneratedCodeAttributes.Line);
            sb.AppendLine(
                indentLevel,
                $"public {typeName}(WorldAccessor world, EntityHandle entityHandle)"
            );
            sb.AppendLine(indentLevel + 1, ": this(world, entityHandle.ToIndex(world))");
            sb.AppendLine(indentLevel, "{");
            sb.AppendLine(indentLevel, "}");
            sb.AppendLine();
        }

        private static void GenerateProperties(
            OptimizedStringBuilder sb,
            int indentLevel,
            AspectComponents components
        )
        {
            foreach (var c in components.Read)
            {
                var access = $"ref {c.BufferFieldName}[__entityIndex.Index]{c.UnwrapAccessSuffix}";
                sb.AppendProperty(
                    c.ReadReturnType,
                    c.PropertyName,
                    access,
                    indentLevel,
                    isInlined: true,
                    attributeLine: GeneratedCodeAttributes.Line
                );
                sb.AppendLine();
            }

            foreach (var c in components.Write)
            {
                var access = $"ref {c.BufferFieldName}[__entityIndex.Index]{c.UnwrapAccessSuffix}";
                sb.AppendProperty(
                    c.WriteReturnType,
                    c.PropertyName,
                    access,
                    indentLevel,
                    isInlined: true,
                    attributeLine: GeneratedCodeAttributes.Line
                );
                sb.AppendLine();
            }
        }

        private static void GenerateHelperMethods(
            OptimizedStringBuilder sb,
            int indentLevel,
            string typeName,
            EquatableArray<ComponentModel> allComponents
        )
        {
            GenerateAspectQueryAndEnumerator(sb, indentLevel, typeName, allComponents);

            // EntityIndex property
            sb.AppendProperty(
                "EntityIndex",
                "EntityIndex",
                "__entityIndex",
                indentLevel,
                isInlined: true,
                attributeLine: GeneratedCodeAttributes.Line
            );
            sb.AppendLine();

            // Index property
            sb.AppendProperty(
                "int",
                "Index",
                "__entityIndex.Index",
                indentLevel,
                isInlined: true,
                attributeLine: GeneratedCodeAttributes.Line
            );
            sb.AppendLine();

            GenerateEntityOperationMethods(sb, indentLevel);
        }

        /// <summary>
        /// Emits generic <c>SetTag&lt;T&gt;</c> / <c>UnsetTag&lt;T&gt;</c> for both
        /// <c>WorldAccessor</c> and <c>NativeWorldAccessor</c>. The 1-arg <c>Remove</c> and
        /// <c>SetTag(TagSet)</c> / <c>UnsetTag(TagSet)</c> live in <c>AspectExtensions</c>; only the generic forms
        /// need per-aspect emission because C# cannot infer the concrete aspect type through
        /// a generic extension method on the open type parameter. Set membership goes
        /// through <c>World.Set&lt;TSet&gt;()</c>, so the aspect doesn't pre-bake either
        /// the deferred or immediate path.
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
                sb.AppendLine(indentLevel, "[MethodImpl(MethodImplOptions.AggressiveInlining)]");
                sb.AppendLine(indentLevel, GeneratedCodeAttributes.Line);
                sb.AppendLine(
                    indentLevel,
                    $"public readonly void SetTag<T>({paramPrefix}{accessorType} world) where T : struct, ITag => __entityIndex.SetTag<T>(world);"
                );
                sb.AppendLine();

                sb.AppendLine(indentLevel, "[MethodImpl(MethodImplOptions.AggressiveInlining)]");
                sb.AppendLine(indentLevel, GeneratedCodeAttributes.Line);
                sb.AppendLine(
                    indentLevel,
                    $"public readonly void UnsetTag<T>({paramPrefix}{accessorType} world) where T : struct, ITag => __entityIndex.UnsetTag<T>(world);"
                );
                sb.AppendLine();
            }
        }

        private static void GenerateAspectQueryAndEnumerator(
            OptimizedStringBuilder sb,
            int indentLevel,
            string typeName,
            EquatableArray<ComponentModel> allComponents
        )
        {
            // --- Query(WorldAccessor) static entry point ---
            sb.AppendLine(indentLevel, GeneratedCodeAttributes.Line);
            sb.AppendLine(indentLevel, "public static AspectQuery Query(WorldAccessor world)");
            sb.AppendLine(indentLevel + 1, "=> new AspectQuery(world.Query());");
            sb.AppendLine();

            // =====================================================================
            // AspectQuery ref struct — fluent builder with filter + terminal methods
            // =====================================================================
            sb.AppendLine(indentLevel, GeneratedCodeAttributes.Line);
            sb.AppendLine(indentLevel, "public ref struct AspectQuery");
            sb.AppendLine(indentLevel, "{");

            sb.AppendLine(indentLevel + 1, "QueryBuilder _builder;");
            sb.AppendLine(indentLevel + 1, "SetId _set;");
            sb.AppendLine(indentLevel + 1, "bool _hasSet;");
            sb.AppendLine();

            sb.AppendLine(indentLevel + 1, "internal AspectQuery(QueryBuilder builder)");
            sb.AppendLine(indentLevel + 1, "{");
            sb.AppendLine(indentLevel + 2, "_builder = builder;");
            sb.AppendLine(indentLevel + 2, "_set = default;");
            sb.AppendLine(indentLevel + 2, "_hasSet = false;");
            sb.AppendLine(indentLevel + 1, "}");
            sb.AppendLine();

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
                "TrecsDebugAssert.That(!_hasSet, \"Only one set per query is supported.\");"
            );
            sb.AppendLine(indentLevel + 2, "_set = EntitySet<T>.Value.Id;");
            sb.AppendLine(indentLevel + 2, "_hasSet = true;");
            sb.AppendLine(indentLevel + 2, "return this;");
            sb.AppendLine(indentLevel + 1, "}");
            sb.AppendLine();

            sb.AppendLine(indentLevel + 1, "public AspectQuery InSet(EntitySet entitySet)");
            sb.AppendLine(indentLevel + 1, "{");
            sb.AppendLine(
                indentLevel + 2,
                "TrecsDebugAssert.That(!_hasSet, \"Only one set per query is supported.\");"
            );
            sb.AppendLine(indentLevel + 2, "_set = entitySet.Id;");
            sb.AppendLine(indentLevel + 2, "_hasSet = true;");
            sb.AppendLine(indentLevel + 2, "return this;");
            sb.AppendLine(indentLevel + 1, "}");
            sb.AppendLine();

            sb.AppendLine(indentLevel + 1, "public AspectQuery InSet(SetId setId)");
            sb.AppendLine(indentLevel + 1, "{");
            sb.AppendLine(
                indentLevel + 2,
                "TrecsDebugAssert.That(!_hasSet, \"Only one set per query is supported.\");"
            );
            sb.AppendLine(indentLevel + 2, "_set = setId;");
            sb.AppendLine(indentLevel + 2, "_hasSet = true;");
            sb.AppendLine(indentLevel + 2, "return this;");
            sb.AppendLine(indentLevel + 1, "}");
            sb.AppendLine();

            // --- MatchByComponents() — adds WithComponents for every component the aspect declares ---
            sb.AppendLine(indentLevel + 1, "public AspectQuery MatchByComponents()");
            sb.AppendLine(indentLevel + 1, "{");
            foreach (var c in allComponents)
                sb.AppendLine(
                    indentLevel + 2,
                    $"_builder = _builder.WithComponents<{c.DisplayString}>();"
                );
            sb.AppendLine(indentLevel + 2, "return this;");
            sb.AppendLine(indentLevel + 1, "}");
            sb.AppendLine();

            // --- Single() terminal ---
            sb.AppendLine(indentLevel + 1, $"public {typeName} Single()");
            sb.AppendLine(indentLevel + 1, "{");
            sb.AppendLine(
                indentLevel + 2,
                $"TrecsDebugAssert.That(_builder.HasAnyCriteria || _hasSet, \"{typeName}.Query requires at least one constraint before calling Single().\");"
            );
            sb.AppendLine(indentLevel + 2, "EntityIndex __ei;");
            sb.AppendLine(indentLevel + 2, "if (_hasSet)");
            sb.AppendLine(indentLevel + 2, "{");
            sb.AppendLine(indentLevel + 3, "var __sb = _builder.InSet(_set);");
            sb.AppendLine(indentLevel + 3, "__ei = __sb.SingleIndex();");
            sb.AppendLine(indentLevel + 2, "}");
            sb.AppendLine(indentLevel + 2, "else");
            sb.AppendLine(indentLevel + 2, "{");
            sb.AppendLine(indentLevel + 3, "__ei = _builder.SingleIndex();");
            sb.AppendLine(indentLevel + 2, "}");
            sb.AppendLine(indentLevel + 2, "var __world = _builder.World;");

            EmitBufferAssignments(
                sb,
                indentLevel + 2,
                allComponents,
                groupExpression: "__ei.GroupIndex",
                worldExpression: "__world",
                lhs: c => c.BufferParamName,
                isFieldAssignment: false
            );

            var ctorArgs = new List<string> { "__ei" };
            foreach (var c in allComponents)
                ctorArgs.Add(c.BufferParamName);
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
                $"TrecsDebugAssert.That(_builder.HasAnyCriteria || _hasSet, \"{typeName}.Query requires at least one constraint before calling TrySingle().\");"
            );
            sb.AppendLine(indentLevel + 2, "bool found;");
            sb.AppendLine(indentLevel + 2, "EntityIndex __ei;");
            sb.AppendLine(indentLevel + 2, "if (_hasSet)");
            sb.AppendLine(indentLevel + 2, "{");
            sb.AppendLine(indentLevel + 3, "var __sb = _builder.InSet(_set);");
            sb.AppendLine(indentLevel + 3, "found = __sb.TrySingleIndex(out __ei);");
            sb.AppendLine(indentLevel + 2, "}");
            sb.AppendLine(indentLevel + 2, "else");
            sb.AppendLine(indentLevel + 2, "{");
            sb.AppendLine(indentLevel + 3, "found = _builder.TrySingleIndex(out __ei);");
            sb.AppendLine(indentLevel + 2, "}");
            sb.AppendLine(indentLevel + 2, "if (!found) { view = default; return false; }");
            sb.AppendLine(indentLevel + 2, "var __world = _builder.World;");

            EmitBufferAssignments(
                sb,
                indentLevel + 2,
                allComponents,
                groupExpression: "__ei.GroupIndex",
                worldExpression: "__world",
                lhs: c => c.BufferParamName,
                isFieldAssignment: false
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
                $"TrecsDebugAssert.That(_builder.HasAnyCriteria || _hasSet, \"{typeName}.Query requires at least one constraint before calling Count().\");"
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
                $"TrecsDebugAssert.That(_builder.HasAnyCriteria || _hasSet, \"{typeName}.Query requires at least one constraint before iterating.\");"
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
            sb.AppendLine(indentLevel, GeneratedCodeAttributes.Line);
            sb.AppendLine(indentLevel, "public ref struct Enumerator");
            sb.AppendLine(indentLevel, "{");

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

            foreach (var c in allComponents)
            {
                sb.AppendLine(
                    indentLevel + 2,
                    $"var {c.BufferParamName} = _world.ComponentBuffer<{c.DisplayString}>(__group).{c.ComponentBufferSuffix};"
                );
            }
            sb.AppendLine();

            var groupCtorArgs = new List<string> { "__group" };
            foreach (var c in allComponents)
                groupCtorArgs.Add(c.BufferParamName);
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

            foreach (var c in allComponents)
            {
                sb.AppendLine(
                    indentLevel + 4,
                    $"var {c.BufferParamName} = _world.ComponentBuffer<{c.DisplayString}>(__group).{c.ComponentBufferSuffix};"
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
        /// Emits a nested <c>NativeFactory</c> struct bundling one
        /// <c>NativeComponentLookupRead/Write&lt;T&gt;</c> per component plus a
        /// <c>Create(EntityIndex)</c> method. Declare as a <c>[FromWorld]</c> field on
        /// a Trecs job struct for cross-entity aspect lookups in Burst code.
        /// </summary>
        private static void GenerateNativeFactory(
            OptimizedStringBuilder sb,
            int indentLevel,
            string aspectName,
            EquatableArray<ComponentModel> allComponents
        )
        {
            if (allComponents.Length == 0)
                return;

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
            sb.AppendLine(indentLevel, GeneratedCodeAttributes.Line);
            sb.AppendLine(indentLevel, "public struct NativeFactory : System.IDisposable");
            sb.AppendLine(indentLevel, "{");

            // Fields — one lookup per component
            for (int i = 0; i < allComponents.Length; i++)
            {
                var c = allComponents[i];
                if (!c.IsReadOnly)
                    sb.AppendLine(
                        indentLevel + 1,
                        "[Unity.Collections.NativeDisableParallelForRestriction]"
                    );
                sb.AppendLine(indentLevel + 1, $"{c.LookupTypeName} _lookup{i};");
            }

            sb.AppendLine();

            // Constructor
            sb.AppendLine(
                indentLevel + 1,
                "[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]"
            );
            {
                var ctorSig = new StringBuilder("public NativeFactory(");
                for (int i = 0; i < allComponents.Length; i++)
                {
                    if (i > 0)
                        ctorSig.Append(", ");
                    ctorSig.Append(allComponents[i].LookupTypeName).Append(" lookup").Append(i);
                }
                ctorSig.Append(")");
                sb.AppendLine(indentLevel + 1, ctorSig.ToString());
            }
            sb.AppendLine(indentLevel + 1, "{");
            for (int i = 0; i < allComponents.Length; i++)
                sb.AppendLine(indentLevel + 2, $"_lookup{i} = lookup{i};");
            sb.AppendLine(indentLevel + 1, "}");

            sb.AppendLine();

            // Create method
            sb.AppendLine(indentLevel + 1, $"public {aspectName} Create(EntityIndex entityIndex)");
            sb.AppendLine(indentLevel + 1, "{");
            sb.AppendLine(indentLevel + 2, $"return new {aspectName}(");
            sb.AppendLine(indentLevel + 3, "entityIndex,");
            for (int i = 0; i < allComponents.Length; i++)
            {
                var suffix = (i == allComponents.Length - 1) ? ");" : ",";
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
            for (int i = 0; i < allComponents.Length; i++)
                sb.AppendLine(indentLevel + 2, $"_lookup{i}.Dispose();");
            sb.AppendLine(indentLevel + 1, "}");

            sb.AppendLine(indentLevel, "}");
        }

        /// <summary>
        /// Emits a sequence of <c>var name = world.ComponentBuffer&lt;T&gt;(group).Read|Write</c>
        /// (or <c>name = ...;</c> for field assignment) lines, one per component, used by all
        /// the buffer-population sites: constructors, Single/TrySingle terminals, and dense
        /// enumerator slice advancement.
        /// </summary>
        private static void EmitBufferAssignments(
            OptimizedStringBuilder sb,
            int indentLevel,
            EquatableArray<ComponentModel> components,
            string groupExpression,
            string worldExpression,
            System.Func<ComponentModel, string> lhs,
            bool isFieldAssignment
        )
        {
            var prefix = isFieldAssignment ? "" : "var ";
            foreach (var c in components)
            {
                sb.AppendLine(
                    indentLevel,
                    $"{prefix}{lhs(c)} = {worldExpression}.ComponentBuffer<{c.DisplayString}>({groupExpression}).{c.ComponentBufferSuffix};"
                );
            }
        }

        // -------------------------------------------------------------------------------------
        // Interface path
        // -------------------------------------------------------------------------------------

        private static void GenerateInterfaceContent(OptimizedStringBuilder sb, AspectModel model)
        {
            var effectiveAccessibility =
                model.Accessibility == "private" ? "internal" : model.Accessibility;

            sb.WrapInContainingTypes(
                model.ContainingTypes.ToArray(),
                0,
                (b, indentLevel) =>
                {
                    b.AppendLine(
                        indentLevel,
                        $"{effectiveAccessibility} partial interface {model.TypeName}"
                    );
                    b.AppendLine(indentLevel, "{");

                    // EntityIndex is inherited from Trecs.IAspect — re-declaring it here would
                    // shadow the base and produce CS0108. The user's partial already lists
                    // IAspect in the base list (that's how we detected this type as an
                    // aspect interface).

                    foreach (var c in model.Components.Read)
                    {
                        b.AppendLine(indentLevel + 1, GeneratedCodeAttributes.Line);
                        b.AppendLine(
                            indentLevel + 1,
                            $"{c.ReadReturnType} {c.PropertyName} {{ get; }}"
                        );
                    }

                    foreach (var c in model.Components.Write)
                    {
                        b.AppendLine(indentLevel + 1, GeneratedCodeAttributes.Line);
                        b.AppendLine(
                            indentLevel + 1,
                            $"{c.WriteReturnType} {c.PropertyName} {{ get; }}"
                        );
                    }

                    b.AppendLine(indentLevel, "}");
                }
            );
        }
    }
}
