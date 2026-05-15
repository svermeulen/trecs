using System.Collections.Generic;
using System.ComponentModel;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface ISystemEntryProvider
    {
        IReadOnlyList<SystemEntry> GetSystemEntries(World world, IReadOnlyList<ISystem> systems);
    }
}
