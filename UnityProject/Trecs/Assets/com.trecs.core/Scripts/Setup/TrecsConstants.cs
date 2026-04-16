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
        public partial class Globals : ITemplate, IHasTags<TrecsTags.Globals> { }
    }
}

namespace Trecs.Internal
{
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class TrecsConstants
    {
        public const int RecordingSentinelValue = 584488256;
    }
}
