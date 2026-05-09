using System.ComponentModel;
using Trecs.Collections;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class WorldAccessorRegistry
    {
        static readonly TrecsLog _log = new(nameof(WorldAccessorRegistry));

        readonly DenseDictionary<ISystem, WorldAccessor> _executeAccessors = new();
        readonly DenseDictionary<int, WorldAccessor> _accessorById = new();

        bool _isClosed;

        public WorldAccessorRegistry() { }

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
            _log.Trace("Registered accessor '{}' with id {}", accessor.DebugName, accessor.Id);
        }

        public void RegisterExecute(ISystem system, WorldAccessor accessor, string debugName)
        {
            Assert.That(!_isClosed);
            Assert.That(
                !_executeAccessors.ContainsKey(system),
                "System {} already has registered execute accessor - secondary accessors should use isSecondary flag",
                debugName
            );

            _executeAccessors.Add(system, accessor);

            _log.Trace("Registered execute accessor for system {}", debugName);
        }

        public WorldAccessor GetAccessorById(int id)
        {
            return _accessorById[id];
        }

        public void Close()
        {
            Assert.That(!_isClosed);
            _isClosed = true;
        }
    }
}
