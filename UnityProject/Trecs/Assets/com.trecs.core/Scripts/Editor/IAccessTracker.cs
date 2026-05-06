using System.Collections.Generic;

namespace Trecs
{
    /// <summary>
    /// Mode-agnostic query surface over component read/write access info and
    /// per-system structural-change tracking. Live mode wraps
    /// <see cref="TrecsAccessTracker"/>; cache mode wraps the pre-recorded
    /// data on <see cref="TrecsSchema.Access"/>. Both flavours key by
    /// component / template <i>display name</i> rather than runtime
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

        // Per-system structural changes — return template DebugNames for the
        // groups the accessor touched via Add/Remove/Move. A move flags both
        // its source and destination templates.
        IReadOnlyCollection<string> GetTemplateNamesAddedBy(string accessorDebugName);
        IReadOnlyCollection<string> GetTemplateNamesRemovedBy(string accessorDebugName);
        IReadOnlyCollection<string> GetTemplateNamesMovedBy(string accessorDebugName);

        // Inverse: given a template DebugName, which systems add/remove/move
        // entities on it? Move covers both ends of the operation.
        IReadOnlyCollection<string> GetSystemsAddingTo(string templateDebugName);
        IReadOnlyCollection<string> GetSystemsRemovingFrom(string templateDebugName);
        IReadOnlyCollection<string> GetSystemsMovingOn(string templateDebugName);
    }
}
