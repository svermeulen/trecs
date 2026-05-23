using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Trecs.SourceGen.Shared
{
    /// <summary>
    /// Value-equality projection of a <see cref="Location"/> for use in incremental-generator
    /// pipeline models. The Roslyn <see cref="Location"/> type is reference-typed and tied to
    /// a specific <see cref="SyntaxTree"/>, so it cannot ride through a cacheable pipeline.
    /// This struct carries only the data needed to reconstruct a usable <see cref="Location"/>
    /// at the terminal source-output stage: file path, character span, and line/column span.
    /// </summary>
    internal readonly record struct LocationInfo(
        string FilePath,
        TextSpan TextSpan,
        LinePositionSpan LineSpan
    )
    {
        /// <summary>
        /// Builds a <see cref="LocationInfo"/> from a Roslyn <see cref="Location"/>. Returns
        /// <see cref="Empty"/> when the location is null or has no source tree (e.g. metadata
        /// location), since those cannot be meaningfully replayed.
        /// </summary>
        public static LocationInfo From(Location? location)
        {
            if (location is null || location.SourceTree is null)
                return Empty;
            return new LocationInfo(
                location.SourceTree.FilePath,
                location.SourceSpan,
                location.GetLineSpan().Span
            );
        }

        /// <summary>
        /// Materializes a <see cref="Location"/> from this info, suitable for passing to
        /// <see cref="Diagnostic.Create(DiagnosticDescriptor, Location, object[])"/>. Returns
        /// <see cref="Location.None"/> when the info has no file path (i.e. it was projected
        /// from a metadata or otherwise non-syntactic location).
        /// </summary>
        public Location ToLocation() =>
            string.IsNullOrEmpty(FilePath)
                ? Location.None
                : Location.Create(FilePath, TextSpan, LineSpan);

        public static readonly LocationInfo Empty = new(string.Empty, default, default);
    }
}
