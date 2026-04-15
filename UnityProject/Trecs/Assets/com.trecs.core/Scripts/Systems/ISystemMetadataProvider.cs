using System.Collections.Generic;
using System.ComponentModel;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface ISystemMetadataProvider
    {
        IReadOnlyList<SystemMetadata> GetSystemMetadata(
            World world,
            IReadOnlyList<ISystem> systems
        );
    }
}
