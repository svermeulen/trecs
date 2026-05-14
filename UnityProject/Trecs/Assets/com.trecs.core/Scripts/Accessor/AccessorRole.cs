using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// What a <see cref="WorldAccessor"/> is allowed to do — controls
    /// component read/write rules, structural-change rules, and heap-allocation rules.
    /// Each value corresponds to a coherent set of permissions:
    /// <list type="bullet">
    /// <item><c>Fixed</c> — simulation. Writes deterministic state, makes structural changes (Add/Remove/Move entity, set ops, SetSystemPaused), allocates persistent heap. Default for fixed-update systems.</item>
    /// <item><c>Variable</c> — display / render. Reads everything (including [VariableUpdateOnly]); writes [VariableUpdateOnly] only; no structural changes; no heap allocation. Default for non-Fixed, non-Input systems.</item>
    /// <item><c>Unrestricted</c> — no role-specific restrictions. For non-system code: lifecycle hooks (<see cref="System.IDisposable.Dispose"/>, init), event callbacks, debug tooling, networking handlers, scripting bridges, and anything else that doesn't fit a tick-phase role. Use sparingly — runtime gameplay code should pick a real role so rule violations surface.</item>
    /// </list>
    /// Input handling is not exposed as a role: input-system permissions
    /// (calling <see cref="WorldAccessor.AddInput{T}"/>, allocating
    /// frame-scoped heap) are auto-enabled on system-owned accessors whose
    /// system declares <c>[ExecuteIn(SystemPhase.Input)]</c>. Manual accessors
    /// created via <see cref="World.CreateAccessor(AccessorRole, string)"/>
    /// never gain input permissions; reach for <c>Unrestricted</c> if you
    /// genuinely need to enqueue inputs from non-system code.
    /// </summary>
    public enum AccessorRole
    {
        Fixed,
        Variable,
        Unrestricted,
    }

    public static class SystemPhaseExtensions
    {
        /// <summary>
        /// Maps a <see cref="SystemPhase"/> to the <see cref="AccessorRole"/>
        /// a system in that phase runs with. <c>Input</c> collapses to
        /// <see cref="AccessorRole.Variable"/> — the input-specific
        /// permissions (frame-scoped heap, <see cref="WorldAccessor.AddInput{T}"/>)
        /// are gated by a separate auto-derived flag, not by the role.
        /// The three Variable-cadence phases
        /// (<c>EarlyPresentation</c>, <c>Presentation</c>, <c>LatePresentation</c>)
        /// also collapse to <see cref="AccessorRole.Variable"/>.
        /// </summary>
        public static AccessorRole ToAccessorRole(this SystemPhase phase)
        {
            return phase switch
            {
                SystemPhase.Input
                or SystemPhase.EarlyPresentation
                or SystemPhase.Presentation
                or SystemPhase.LatePresentation => AccessorRole.Variable,
                SystemPhase.Fixed => AccessorRole.Fixed,
                _ => throw TrecsAssert.CreateException("Unknown SystemPhase {0}", phase),
            };
        }
    }
}
