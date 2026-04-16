namespace Trecs
{
    /// <summary>
    /// Marker interface for entity component types. Implementing structs must be
    /// <c>unmanaged</c> — non-unmanaged structs will not be recognized by the framework.
    /// Components are stored in contiguous native buffers per-group and accessed via
    /// <see cref="WorldAccessor"/> or aspect types.
    /// </summary>
    public interface IEntityComponent { }
}
