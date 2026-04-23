using System;
using Trecs.Internal;

namespace Trecs
{
    // Marked [VariableUpdate] because this system *drives* the fixed-update loop
    // from the variable-update phase — each variable tick it dispatches 0..N
    // fixed ticks based on accumulated time. The fixed systems themselves run
    // inside _fixedTickHandler, not as independent [VariableUpdate] systems.
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
