using System.ComponentModel;
using Trecs.Collections;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class WorldAccessorRegistry
    {
        readonly TrecsLog _log;

        readonly DenseDictionary<ISystem, WorldAccessor> _executeAccessors = new();
        readonly DenseDictionary<int, WorldAccessor> _accessorById = new();

        bool _isClosed;

        public WorldAccessorRegistry(TrecsLog log)
        {
            _log = log;
        }

        public ReadOnlyDenseDictionary<ISystem, WorldAccessor> ExecuteAccessors
        {
            get { return _executeAccessors; }
        }

        public ReadOnlyDenseDictionary<int, WorldAccessor> AccessorsById
        {
            get { return _accessorById; }
        }

        public void RegisterById(WorldAccessor accessor)
        {
            _accessorById.Add(accessor.Id, accessor);
            _log.Trace("Registered accessor '{0}' with id {1}", accessor.DebugName, accessor.Id);
        }

        public void RegisterExecute(ISystem system, WorldAccessor accessor, string debugName)
        {
            TrecsAssert.That(!_isClosed);
            TrecsAssert.That(
                !_executeAccessors.ContainsKey(system),
                "System {0} already has registered execute accessor - secondary accessors should use isSecondary flag",
                debugName
            );

            _executeAccessors.Add(system, accessor);

            _log.Trace("Registered execute accessor for system {0}", debugName);
        }

        public WorldAccessor GetAccessorById(int id)
        {
            return _accessorById[id];
        }

        public void Close()
        {
            TrecsAssert.That(!_isClosed);
            _isClosed = true;
        }
    }
}
