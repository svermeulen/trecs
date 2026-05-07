using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Serialization.Samples.SaveGame
{
    /// <summary>
    /// Sokoban-style tap input: one move per keypress, not continuous.
    /// GetKeyDown fires only on the frame the key transitioned from up to
    /// down, so holding a key won't auto-repeat moves.
    /// </summary>
    [ExecuteIn(SystemPhase.Input)]
    public partial class PlayerInputSystem : ISystem
    {
        int2 _pendingStep;

        public void Tick()
        {
            if (Input.GetKeyDown(KeyCode.W) || Input.GetKeyDown(KeyCode.UpArrow))
            {
                _pendingStep = new int2(0, 1);
            }
            else if (Input.GetKeyDown(KeyCode.S) || Input.GetKeyDown(KeyCode.DownArrow))
            {
                _pendingStep = new int2(0, -1);
            }
            else if (Input.GetKeyDown(KeyCode.A) || Input.GetKeyDown(KeyCode.LeftArrow))
            {
                _pendingStep = new int2(-1, 0);
            }
            else if (Input.GetKeyDown(KeyCode.D) || Input.GetKeyDown(KeyCode.RightArrow))
            {
                _pendingStep = new int2(1, 0);
            }
        }

        public void Execute()
        {
            if (_pendingStep.x == 0 && _pendingStep.y == 0)
            {
                return;
            }
            World.AddInput(World.GlobalEntityHandle, new MoveInput { Step = _pendingStep });
            _pendingStep = int2.zero;
        }
    }
}
