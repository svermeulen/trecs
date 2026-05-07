#nullable enable

using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Trecs.SourceGen.Performance;
using Trecs.SourceGen.Shared;

namespace Trecs.SourceGen
{
    /// <summary>
    /// Incremental source generator for interpolator jobs that generate interpolated updater systems.
    /// Provides better compilation performance than the legacy InterpolationGenerator.
    /// </summary>
    [Generator]
    public class InterpolatorJobGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Check if compilation references Trecs assembly for better performance
            var hasTrecsReference = AssemblyFilterHelper.CreateTrecsReferenceCheck(context);

            // Create provider for methods with GenerateInterpolatorSystemAttribute
            var interpolatorMethodProviderRaw = context
                .SyntaxProvider.ForAttributeWithMetadataName(
                    "Trecs.GenerateInterpolatorSystemAttribute",
                    predicate: static (node, _) => node is MethodDeclarationSyntax,
                    transform: static (ctx, _) => GetInterpolatorMethodData(ctx)
                )
                .Where(static m => m is not null);
            var interpolatorMethodProvider = AssemblyFilterHelper.FilterByTrecsReference(
                interpolatorMethodProviderRaw,
                hasTrecsReference
            );

            // Combine with compilation provider
            var interpolatorMethodWithCompilation = interpolatorMethodProvider.Combine(
                context.CompilationProvider
            );

            // Register source output
            context.RegisterSourceOutput(
                interpolatorMethodWithCompilation,
                static (spc, source) => GenerateInterpolatorSource(spc, source.Left!, source.Right)
            );
        }

        private static InterpolatorMethodData? GetInterpolatorMethodData(
            GeneratorAttributeSyntaxContext context
        )
        {
            var methodDecl = (MethodDeclarationSyntax)context.TargetNode;
            var methodSymbol = context.TargetSymbol as IMethodSymbol;

            if (methodSymbol == null)
                return null;

            var attribute = context.Attributes.FirstOrDefault();
            if (attribute == null)
                return null;

            return new InterpolatorMethodData(methodDecl, methodSymbol, attribute);
        }

        private static void GenerateInterpolatorSource(
            SourceProductionContext context,
            InterpolatorMethodData data,
            Compilation compilation
        )
        {
            var location = data.MethodDecl.GetLocation();
            var methodSymbol = data.MethodSymbol;
            var attributeData = data.AttributeData;

            try
            {
                using var _timer_ = SourceGenTimer.Time("InterpolatorJobGenerator.Total");
                // Extract class name from attribute
                if (
                    attributeData.ConstructorArguments.Length == 0
                    || attributeData.ConstructorArguments[0].Value is not string className
                )
                {
                    SourceGenLogger.Log(
                        $"[InterpolatorJobGenerator] Missing or invalid className in attribute for {methodSymbol.Name}"
                    );
                    return;
                }

                SourceGenLogger.Log(
                    $"[InterpolatorJobGenerator] Processing {methodSymbol.Name} -> {className}"
                );

                // Generate the interpolated updater source code
                var source = GenerateInterpolatedUpdater(methodSymbol, className);
                var fileName = SymbolAnalyzer.GetSafeFileName(
                    methodSymbol.ContainingType,
                    className
                );

                context.AddSource(fileName, source);
                SourceGenLogger.WriteGeneratedFile(fileName, source);
            }
            catch (Exception ex)
            {
                SourceGenLogger.Log(
                    $"[InterpolatorJobGenerator] Error generating code for {methodSymbol.Name}: {ex.Message}"
                );

                // Report error for any unhandled exceptions
                var diagnostic = Diagnostic.Create(
                    DiagnosticDescriptors.CouldNotResolveSymbol,
                    location,
                    $"{methodSymbol.Name}: {ex.Message}"
                );
                context.ReportDiagnostic(diagnostic);
            }
        }

        private static string GenerateInterpolatedUpdater(
            IMethodSymbol methodSymbol,
            string className
        )
        {
            string nameSpace = PerformanceCache.GetDisplayString(methodSymbol.ContainingNamespace);
            string componentName = PerformanceCache.GetDisplayString(
                methodSymbol.Parameters[0].Type
            );
            string interpolatorMethodName =
                $"{PerformanceCache.GetDisplayString(methodSymbol.ContainingType)}.{methodSymbol.Name}";

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
    public class {className} : ISystem, Trecs.Internal.ISystemInternal
    {{
        WorldAccessor _world;

        public WorldAccessor World => _world;

        WorldAccessor Trecs.Internal.ISystemInternal.World
        {{
            get => _world;
            set {{ Assert.That(_world == null, ""World has already been set""); _world = value; }}
        }}

        public {className}()
        {{
        }}

        void Trecs.Internal.ISystemInternal.Ready()
        {{
        }}

        public void Execute()
        {{
            var percentThroughFixedFrame = InterpolationUtil.CalculatePercentThroughFixedFrame(_world);
            using var jobHandles = new NativeList<JobHandle>(4, Allocator.Temp);
            // Route through Trecs.Internal.JobGenSchedulingExtensions so this generated
            // code compiles in user assemblies that aren't friend-accessed by Trecs.
            var _trecs_scheduler = _world.GetJobSchedulerForJob();
            var _trecs_currentRid = ResourceId.Component(ComponentTypeId<{componentName}>.Value);
            var _trecs_previousRid = ResourceId.Component(ComponentTypeId<InterpolatedPrevious<{componentName}>>.Value);
            var _trecs_interpRid = ResourceId.Component(ComponentTypeId<Interpolated<{componentName}>>.Value);

            foreach (var group in _world.WorldInfo.GetGroupsWithComponents<{componentName}, InterpolatedPrevious<{componentName}>, Interpolated<{componentName}>>())
            {{
                var (currents, count) = _world.GetBufferReadForJob<{componentName}>(group);
                if (count == 0) continue;
                var (previouses, _) = _world.GetBufferReadForJob<InterpolatedPrevious<{componentName}>>(group);
                var (interpolates, _) = _world.GetBufferWriteForJob<Interpolated<{componentName}>>(group);

                var _trecs_deps = default(JobHandle);
                _trecs_deps = _trecs_scheduler.IncludeReadDep(_trecs_deps, _trecs_currentRid, group);
                _trecs_deps = _trecs_scheduler.IncludeReadDep(_trecs_deps, _trecs_previousRid, group);
                _trecs_deps = _trecs_scheduler.IncludeWriteDep(_trecs_deps, _trecs_interpRid, group);

                var _trecs_handle = new InterpolateJob()
                {{
                    CurrentValues = currents,
                    InterpolatedValues = interpolates,
                    PreviousValues = previouses,
                    PercentThroughFixedFrame = percentThroughFixedFrame,
                }}.ScheduleParallel(count, JobsUtil.ChooseBatchSize(count), _trecs_deps);

                _trecs_scheduler.TrackJobRead(_trecs_handle, _trecs_currentRid, group);
                _trecs_scheduler.TrackJobRead(_trecs_handle, _trecs_previousRid, group);
                _trecs_scheduler.TrackJobWrite(_trecs_handle, _trecs_interpRid, group);

                jobHandles.Add(_trecs_handle);
            }}
        }}

        [BurstCompile]
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
    /// Data structure for interpolator method information used in incremental generation
    /// </summary>
    internal class InterpolatorMethodData
    {
        public MethodDeclarationSyntax MethodDecl { get; }
        public IMethodSymbol MethodSymbol { get; }
        public AttributeData AttributeData { get; }

        public InterpolatorMethodData(
            MethodDeclarationSyntax methodDecl,
            IMethodSymbol methodSymbol,
            AttributeData attributeData
        )
        {
            MethodDecl = methodDecl;
            MethodSymbol = methodSymbol;
            AttributeData = attributeData;
        }
    }
}
