using System;
using Trecs.Internal;

namespace Trecs
{
    [VariableUpdate]
    public partial class FixedUpdateSystem : ISystem
    {
        Action _fixedTickHandler;

        public FixedUpdateSystem() { }

        internal Action FixedTickHandler
        {
            set
            {
                Assert.IsNull(_fixedTickHandler);
                _fixedTickHandler = value;
            }
        }

        public void Execute()
        {
            _fixedTickHandler();
        }
    }
}
