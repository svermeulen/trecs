using System;
using System.Collections.Generic;
using System.Linq;

namespace Trecs
{
    /// <summary>
    /// Explicit ordering constraint that forces a sequence of systems to execute in the
    /// specified order during topological sorting. Register instances via
    /// <see cref="WorldBuilder"/> to impose cross-system ordering guarantees.
    /// </summary>
    public sealed class SystemOrderConstraint
    {
        public readonly IReadOnlyList<Type> SystemOrder;

        public SystemOrderConstraint(params Type[] systemOrder)
        {
            SystemOrder = systemOrder;
        }

        public string ToDebugString()
        {
            return string.Join(" -> ", SystemOrder.Select(t => t.Name));
        }

        public List<Type> FlattenSystemOrder()
        {
            return new List<Type>(SystemOrder);
        }
    }
}
