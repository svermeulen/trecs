using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Serialization.Samples.Snake
{
    /// <summary>
    /// Reads arrow-key presses each variable frame and pushes a MoveInput
    /// onto the global entity for the next fixed step. Modeled after
    /// FeedingFrenzy's FishCountPresetInputSystem: Tick() captures input each Unity
    /// Update, Execute() runs once per fixed frame and forwards via
    /// World.AddInput.
    /// </summary>
    [InputSystem]
    public partial class SnakeInputSystem : ISystem
    {
        int2 _pendingDirection;

        public void Tick()
        {
            if (Input.GetKeyDown(KeyCode.W))
            {
                _pendingDirection = new int2(0, 1);
            }
            else if (Input.GetKeyDown(KeyCode.S))
            {
                _pendingDirection = new int2(0, -1);
            }
            else if (Input.GetKeyDown(KeyCode.A))
            {
                _pendingDirection = new int2(-1, 0);
            }
            else if (Input.GetKeyDown(KeyCode.D))
            {
                _pendingDirection = new int2(1, 0);
            }
        }

        public void Execute()
        {
            if (_pendingDirection.x != 0 || _pendingDirection.y != 0)
            {
                World.AddInput(
                    World.GlobalEntityHandle,
                    new MoveInput { RequestedDirection = _pendingDirection }
                );
                _pendingDirection = int2.zero;
            }
        }
    }
}
