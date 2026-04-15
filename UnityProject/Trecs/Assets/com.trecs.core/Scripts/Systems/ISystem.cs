using System.ComponentModel;

namespace Trecs
{
    public interface ISystem
    {
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
