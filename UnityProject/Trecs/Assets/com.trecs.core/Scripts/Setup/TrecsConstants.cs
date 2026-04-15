namespace Trecs
{
    public static class TrecsTags
    {
        public struct Globals : ITag { }
    }

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
