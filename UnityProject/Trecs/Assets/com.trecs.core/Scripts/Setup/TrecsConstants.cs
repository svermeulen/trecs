using System.ComponentModel;

namespace Trecs
{
    /// <summary>
    /// Built-in tags provided by the Trecs framework.
    /// </summary>
    public static class TrecsTags
    {
        public struct Globals : ITag { }
    }

    /// <summary>
    /// Built-in templates provided by the Trecs framework.
    /// </summary>
    public static partial class TrecsTemplates
    {
        public partial class Globals : ITemplate, ITagged<TrecsTags.Globals> { }
    }
}

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class TrecsConstants
    {
        public const int RecordingSentinelValue = 584488256;

        /// <summary>
        /// Wire-format version of <see cref="RecordingBundle"/>'s on-disk
        /// layout. Bumped whenever the bundle's binary serialization changes
        /// incompatibly (new fields, reordering, type changes). Distinct
        /// from <see cref="BundleHeader.Version"/> (user-defined schema
        /// version) and from the Layer-1 SerializationHeader format version
        /// (which guards the magic-byte envelope shared by all Trecs
        /// payloads).
        ///
        /// On load, <see cref="RecordingBundleSerializer"/> compares this
        /// constant against the bundle's stored version and rejects with a
        /// <see cref="SerializationException"/> on mismatch — failing
        /// loudly beats silently misinterpreting an older layout.
        /// </summary>
        public const byte CurrentBundleFormatVersion = 5;
    }
}
