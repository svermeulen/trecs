#nullable enable

using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Trecs.SourceGen.Performance;
using Trecs.SourceGen.Shared;

namespace Trecs.SourceGen
{
    /// <summary>
    /// Incremental source generator for interpolator jobs that generates interpolated updater
    /// systems. Provides better compilation performance than the legacy InterpolationGenerator.
    ///
    /// <para>Pipeline shape: the transform produces a value-equatable
    /// <see cref="InterpolatorJobModel"/> and the terminal stage emits source. Previously
    /// the pipeline carried <see cref="MethodDeclarationSyntax"/>, <see cref="IMethodSymbol"/>,
    /// and <see cref="AttributeData"/> through to source-output, which prevented Roslyn's
    /// incremental cache from hitting on any edit. The old <c>.Combine(CompilationProvider)</c>
    /// is also gone — the compilation parameter was passed to the terminal stage but never
    /// actually used.</para>
    /// </summary>
    [Generator]
    public class InterpolatorJobGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var hasTrecsReference = AssemblyFilterHelper.CreateTrecsReferenceCheck(context);

            var modelsRaw = context
                .SyntaxProvider.ForAttributeWithMetadataName(
                    "Trecs.GenerateInterpolatorSystemAttribute",
                    predicate: static (node, _) => node is MethodDeclarationSyntax,
                    transform: static (ctx, _) => BuildModel(ctx)
                )
                .Where(static m => m is not null)
                .Select(static (m, _) => m!.Value);
            var models = AssemblyFilterHelper.FilterByTrecsReference(modelsRaw, hasTrecsReference);

            context.RegisterSourceOutput(
                models,
                static (spc, model) => GenerateInterpolatorSource(spc, model)
            );
        }

        private static InterpolatorJobModel? BuildModel(GeneratorAttributeSyntaxContext context)
        {
            if (context.TargetSymbol is not IMethodSymbol methodSymbol)
                return null;
            var attribute = context.Attributes.FirstOrDefault();
            if (attribute == null)
                return null;
            if (methodSymbol.Parameters.Length == 0)
                return null;

            // className comes from the attribute's first ctor arg. Bail if it's missing or
            // not a string — the previous code did the same check inside the terminal stage
            // and silently returned, but doing it here means we don't carry a half-built
            // model through the pipeline at all.
            if (
                attribute.ConstructorArguments.Length == 0
                || attribute.ConstructorArguments[0].Value is not string className
            )
            {
                return null;
            }

            return new InterpolatorJobModel(
                MethodName: methodSymbol.Name,
                ContainingTypeDisplay: PerformanceCache.GetDisplayString(
                    methodSymbol.ContainingType
                ),
                ContainingTypeFileName: SymbolAnalyzer.GetSafeFileName(
                    methodSymbol.ContainingType,
                    className
                ),
                Namespace: PerformanceCache.GetDisplayString(methodSymbol.ContainingNamespace),
                ComponentTypeDisplay: PerformanceCache.GetDisplayString(
                    methodSymbol.Parameters[0].Type
                ),
                ClassName: className
            );
        }

        private static void GenerateInterpolatorSource(
            SourceProductionContext context,
            InterpolatorJobModel model
        )
        {
            try
            {
                using var _timer_ = SourceGenTimer.Time("InterpolatorJobGenerator.Total");
                SourceGenLogger.Log(
                    $"[InterpolatorJobGenerator] Processing {model.MethodName} -> {model.ClassName}"
                );

                var source = GenerateInterpolatedUpdater(model);
                context.AddSource(model.ContainingTypeFileName, source);
                SourceGenLogger.WriteGeneratedFile(model.ContainingTypeFileName, source);
            }
            catch (Exception ex)
            {
                SourceGenLogger.Log(
                    $"[InterpolatorJobGenerator] Error generating code for {model.MethodName}: {ex.Message}"
                );

                var diagnostic = Diagnostic.Create(
                    DiagnosticDescriptors.CouldNotResolveSymbol,
                    Location.None,
                    $"{model.MethodName}: {ex.Message}"
                );
                context.ReportDiagnostic(diagnostic);
            }
        }

        private static string GenerateInterpolatedUpdater(InterpolatorJobModel model)
        {
            string interpolatorMethodName = $"{model.ContainingTypeDisplay}.{model.MethodName}";
            string componentName = model.ComponentTypeDisplay;
            string className = model.ClassName;
            string nameSpace = model.Namespace;

            return $@"
using Unity.Jobs;
using Unity.Burst;
using Unity.Collections;
{CommonUsings.AsDirectives}

namespace {nameSpace}
{{
    [ExecuteIn(SystemPhase.Presentation)]
    [ExecutePriority(-1000)]
    [AllowMultiple]
    {GeneratedCodeAttributes.Line}
    public class {className} : ISystem, Trecs.Internal.ISystemInternal
    {{
        WorldAccessor __world;

        public WorldAccessor World => __world;

        WorldAccessor Trecs.Internal.ISystemInternal.World
        {{
            get => __world;
            set {{ TrecsDebugAssert.That(__world == null, ""World has already been set""); __world = value; }}
        }}

        public {className}()
        {{
        }}

        void Trecs.Internal.ISystemInternal.Ready()
        {{
        }}

        void Trecs.Internal.ISystemInternal.Shutdown()
        {{
        }}

        public void Execute()
        {{
            var percentThroughFixedFrame = InterpolationUtil.CalculatePercentThroughFixedFrame(__world);
            using var jobHandles = new NativeList<JobHandle>(4, Allocator.Temp);
            // Route through Trecs.Internal.JobGenSchedulingExtensions so this generated
            // code compiles in user assemblies that aren't friend-accessed by Trecs.
            var __trecs_scheduler = __world.GetJobSchedulerForJob();
            const string __trecs_jobName = ""InterpolateJob<{componentName}>"";
            var __trecs_currentRid = ResourceId.Component(TypeId<{componentName}>.Value);
            var __trecs_previousRid = ResourceId.Component(TypeId<InterpolatedPrevious<{componentName}>>.Value);
            var __trecs_interpRid = ResourceId.Component(TypeId<Interpolated<{componentName}>>.Value);

            foreach (var group in __world.WorldInfo.GetGroupsWithComponents<{componentName}, InterpolatedPrevious<{componentName}>, Interpolated<{componentName}>>())
            {{
                var (currents, count) = __world.GetBufferReadForJob<{componentName}>(group);
                if (count == 0) continue;
                var (previouses, _) = __world.GetBufferReadForJob<InterpolatedPrevious<{componentName}>>(group);
                var (interpolates, _) = __world.GetBufferWriteForJob<Interpolated<{componentName}>>(group);

                var __trecs_deps = default(JobHandle);
                __trecs_deps = __trecs_scheduler.IncludeReadDep(__trecs_deps, __trecs_currentRid, group);
                __trecs_deps = __trecs_scheduler.IncludeReadDep(__trecs_deps, __trecs_previousRid, group);
                __trecs_deps = __trecs_scheduler.IncludeWriteDep(__trecs_deps, __trecs_interpRid, group);

                var __trecs_handle = new InterpolateJob()
                {{
                    CurrentValues = currents,
                    InterpolatedValues = interpolates,
                    PreviousValues = previouses,
                    PercentThroughFixedFrame = percentThroughFixedFrame,
                }}.ScheduleParallel(count, JobsUtil.ChooseBatchSize(count), __trecs_deps);

                __trecs_scheduler.TrackJobRead(__trecs_handle, __trecs_currentRid, group, __trecs_jobName);
                __trecs_scheduler.TrackJobRead(__trecs_handle, __trecs_previousRid, group, __trecs_jobName);
                __trecs_scheduler.TrackJobWrite(__trecs_handle, __trecs_interpRid, group, __trecs_jobName);

                jobHandles.Add(__trecs_handle);
            }}
        }}

        [BurstCompile]
        {GeneratedCodeAttributes.Line}
        internal struct InterpolateJob : IJobFor
        {{
            public NativeComponentBufferRead<{componentName}> CurrentValues;

            public NativeComponentBufferRead<InterpolatedPrevious<{componentName}>> PreviousValues;

            [Unity.Collections.NativeDisableParallelForRestriction]
            public NativeComponentBufferWrite<Interpolated<{componentName}>> InterpolatedValues;
            public float PercentThroughFixedFrame;

            public readonly void Execute(int i)
            {{
                ref readonly var current = ref CurrentValues[i];
                ref readonly var previous = ref PreviousValues[i];
                ref var interpolated = ref InterpolatedValues[i];

                {interpolatorMethodName}(
                    previous.Value, current, ref interpolated.Value, PercentThroughFixedFrame);
            }}
        }}
    }}
}}";
        }
    }

    /// <summary>
    /// Value-equality model carried through the incremental pipeline. All fields are
    /// primitives / strings — no Roslyn symbol or syntax references — so the cache hits
    /// when nothing observable about the interpolator method has changed.
    /// </summary>
    internal readonly record struct InterpolatorJobModel(
        string MethodName,
        string ContainingTypeDisplay,
        string ContainingTypeFileName,
        string Namespace,
        string ComponentTypeDisplay,
        string ClassName
    );
}
