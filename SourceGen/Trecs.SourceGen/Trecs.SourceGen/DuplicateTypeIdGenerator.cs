using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Trecs.SourceGen.Shared;

namespace Trecs.SourceGen
{
    /// <summary>
    /// Incremental source generator that detects collisions between user-supplied
    /// <c>[TypeId(...)]</c> values at compile time. Two types with the same TypeId
    /// silently corrupt saved data at runtime — this generator raises
    /// <see cref="DiagnosticDescriptors.DuplicateTypeId"/> on every participant of
    /// a collision so the user can fix it before shipping.
    ///
    /// The generator emits no source — it only reports diagnostics.
    /// </summary>
    [Generator]
    public class DuplicateTypeIdGenerator : IIncrementalGenerator
    {
        readonly struct TypeIdEntry
        {
            public readonly string TypeName;
            public readonly int Id;
            public readonly Location Location;

            public TypeIdEntry(string typeName, int id, Location location)
            {
                TypeName = typeName;
                Id = id;
                Location = location;
            }
        }

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var hasTrecsReference = AssemblyFilterHelper.CreateTrecsReferenceCheck(context);

            var typeIdProviderRaw = context
                .SyntaxProvider.ForAttributeWithMetadataName(
                    "Trecs.TypeIdAttribute",
                    predicate: static (node, _) =>
                        node is StructDeclarationSyntax
                        || node is ClassDeclarationSyntax
                        || node is EnumDeclarationSyntax,
                    transform: static (context, _) => GetTypeIdEntry(context)
                )
                .Where(static entry => entry != null)
                .Select(static (entry, _) => entry!.Value);

            var typeIdProvider = AssemblyFilterHelper.FilterByTrecsReference(
                typeIdProviderRaw,
                hasTrecsReference
            );

            var allTypeIds = typeIdProvider.Collect();

            context.RegisterSourceOutput(
                allTypeIds,
                static (spc, entries) => ReportCollisions(spc, entries)
            );
        }

        static TypeIdEntry? GetTypeIdEntry(GeneratorAttributeSyntaxContext context)
        {
            if (!(context.TargetSymbol is INamedTypeSymbol typeSymbol))
            {
                return null;
            }

            foreach (var attr in context.Attributes)
            {
                if (attr.ConstructorArguments.Length != 1)
                {
                    continue;
                }

                var arg = attr.ConstructorArguments[0];
                if (!(arg.Value is int id))
                {
                    continue;
                }

                var location =
                    attr.ApplicationSyntaxReference?.GetSyntax().GetLocation()
                    ?? typeSymbol.Locations.FirstOrDefault()
                    ?? Location.None;

                return new TypeIdEntry(
                    typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    id,
                    location
                );
            }

            return null;
        }

        static void ReportCollisions(
            SourceProductionContext spc,
            ImmutableArray<TypeIdEntry> entries
        )
        {
            if (entries.Length < 2)
            {
                return;
            }

            var byId = new Dictionary<int, List<TypeIdEntry>>();
            foreach (var entry in entries)
            {
                if (!byId.TryGetValue(entry.Id, out var list))
                {
                    list = new List<TypeIdEntry>();
                    byId[entry.Id] = list;
                }
                list.Add(entry);
            }

            foreach (var kvp in byId)
            {
                var list = kvp.Value;
                if (list.Count < 2)
                {
                    continue;
                }

                // One diagnostic per participant; each names one of the other
                // colliding types so the user sees the conflict from any end.
                for (int i = 0; i < list.Count; i++)
                {
                    var self = list[i];
                    var other = list[(i + 1) % list.Count];
                    spc.ReportDiagnostic(
                        Diagnostic.Create(
                            DiagnosticDescriptors.DuplicateTypeId,
                            self.Location,
                            self.TypeName,
                            self.Id,
                            other.TypeName
                        )
                    );
                }
            }
        }
    }
}
