using System;
using Microsoft.CodeAnalysis;

namespace Trecs.SourceGen.Shared
{
    /// <summary>
    /// Value-equality projection of a Roslyn <see cref="Diagnostic"/> for use in
    /// incremental-generator pipeline models. <see cref="Diagnostic"/> is reference-typed
    /// and not equatable, so two identical diagnostics produced by two runs of the same
    /// transform compare unequal — which forces every downstream pipeline node to re-run
    /// even when nothing observable changed. Carrying <see cref="DiagnosticInfo"/> instead,
    /// and materializing the real diagnostic only at the terminal <c>RegisterSourceOutput</c>
    /// stage via <see cref="ToDiagnostic"/>, keeps the cache effective.
    ///
    /// <para>The <see cref="DiagnosticDescriptor"/> reference is safe to carry: descriptors
    /// are static fields on <c>DiagnosticDescriptors</c>, so the same instance is reused
    /// across compilations within the same analyzer host (reference equality is sufficient).</para>
    /// </summary>
    internal readonly record struct DiagnosticInfo(
        DiagnosticDescriptor Descriptor,
        LocationInfo Location,
        EquatableArray<string> MessageArgs,
        string? PreformattedMessage = null
    )
    {
        public static DiagnosticInfo Create(
            DiagnosticDescriptor descriptor,
            Location? location,
            params string[] messageArgs
        ) =>
            new(
                descriptor,
                LocationInfo.From(location),
                new EquatableArray<string>(messageArgs ?? Array.Empty<string>())
            );

        public static DiagnosticInfo Create(
            DiagnosticDescriptor descriptor,
            LocationInfo location,
            params string[] messageArgs
        ) =>
            new(
                descriptor,
                location,
                new EquatableArray<string>(messageArgs ?? Array.Empty<string>())
            );

        /// <summary>
        /// Capture an already-built <see cref="Diagnostic"/> by extracting its
        /// pre-formatted message text and stashing it in
        /// <see cref="PreformattedMessage"/>. Use this when an existing helper API
        /// produces <c>Diagnostic</c> objects directly and there's no clean way to
        /// recover the original raw message-args (e.g. <see cref="Shared.ParameterClassifier.ClassifyHoistedSingleton"/>).
        /// <see cref="ToDiagnostic"/> bypasses descriptor-format substitution for these
        /// entries so the message text isn't double-formatted.
        /// </summary>
        public static DiagnosticInfo FromDiagnostic(Diagnostic diagnostic) =>
            new(
                diagnostic.Descriptor,
                LocationInfo.From(diagnostic.Location),
                EquatableArray<string>.Empty,
                diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture)
            );

        /// <summary>
        /// Materializes a real <see cref="Diagnostic"/> from this info. Call at the terminal
        /// <c>RegisterSourceOutput</c> stage where <see cref="SourceProductionContext.ReportDiagnostic"/>
        /// is available.
        /// </summary>
        public Diagnostic ToDiagnostic()
        {
            if (PreformattedMessage != null)
            {
                // Preformatted path: the message is already fully substituted, so we
                // bypass the descriptor's MessageFormat and supply the text directly.
                // Warning-level diagnostics need a non-zero warning level for some IDE
                // hosts to surface them; everything else uses 0.
                return Diagnostic.Create(
                    Descriptor.Id,
                    Descriptor.Category,
                    PreformattedMessage,
                    Descriptor.DefaultSeverity,
                    Descriptor.DefaultSeverity,
                    Descriptor.IsEnabledByDefault,
                    warningLevel: Descriptor.DefaultSeverity == DiagnosticSeverity.Warning ? 1 : 0,
                    title: Descriptor.Title,
                    description: Descriptor.Description,
                    helpLink: Descriptor.HelpLinkUri,
                    location: Location.ToLocation()
                );
            }
            return Diagnostic.Create(Descriptor, Location.ToLocation(), MessageArgs.ToArray());
        }
    }
}
