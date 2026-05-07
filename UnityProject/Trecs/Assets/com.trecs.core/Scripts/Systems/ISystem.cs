using System.ComponentModel;

namespace Trecs
{
    /// <summary>
    /// Core interface implemented by all user systems. Source generation adds a
    /// <see cref="WorldAccessor"/> property and lifecycle wiring; user code only needs
    /// to implement <see cref="Execute"/>. By default systems run in the
    /// <see cref="SystemPhase.Fixed"/> phase — apply <see cref="ExecuteInAttribute"/>
    /// to change the phase.
    /// </summary>
    public interface ISystem
    {
        /// <summary>
        /// Called once per frame in the system's assigned update phase. Access the ECS
        /// world through the source-generated <c>World</c> property (a <see cref="WorldAccessor"/>).
        /// </summary>
        void Execute();
    }
}

namespace Trecs.Internal
{
    /// <summary>
    /// Internal interface for framework to manage system lifecycle.
    /// Implemented by source-generated code. Not intended for user code.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface ISystemInternal
    {
        WorldAccessor World { get; set; }
        void Ready();
    }
}
