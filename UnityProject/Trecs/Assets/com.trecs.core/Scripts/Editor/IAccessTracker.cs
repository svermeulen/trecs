using System.Collections.Generic;

namespace Trecs
{
    /// <summary>
    /// Mode-agnostic query surface over component read/write access info.
    /// Live mode wraps <see cref="TrecsAccessTracker"/>; cache mode wraps the
    /// pre-recorded data on <see cref="TrecsSchema.Access"/>. Both flavours
    /// key by component <i>display name</i> rather than runtime
    /// <see cref="Internal.ComponentId"/> so cache mode (which has no
    /// TypeIdProvider) can answer the same questions.
    /// </summary>
    public interface IAccessTracker
    {
        IReadOnlyCollection<string> GetReadersOfComponent(string componentDisplayName);
        IReadOnlyCollection<string> GetWritersOfComponent(string componentDisplayName);
        IReadOnlyCollection<string> GetComponentsReadBy(string systemName);
        IReadOnlyCollection<string> GetComponentsWrittenBy(string systemName);

        // Tag names whose groups were touched by `accessorDebugName`. Cache
        // mode reads these from the persisted schema; live mode derives them
        // from the runtime tracker + WorldInfo on demand.
        IReadOnlyCollection<string> GetTagNamesTouchedBy(string accessorDebugName);
    }
}
