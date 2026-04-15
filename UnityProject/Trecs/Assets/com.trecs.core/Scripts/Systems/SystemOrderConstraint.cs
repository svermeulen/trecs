using System;
using System.Collections.Generic;
using System.Linq;

namespace Trecs
{
    public class SystemOrderConstraint
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
