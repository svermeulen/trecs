namespace Trecs.Internal
{
    /// <summary>
    /// Interface for types that can provide a stable hash code that remains consistent
    /// across application restarts and process boundaries.
    /// The standard GetHashCode() method is not suitable for this purpose as it can
    /// change between different runs of the application.
    /// </summary>
    public interface IStableHashProvider
    {
        /// <summary>
        /// Returns a hash code that remains stable across application restarts.
        /// </summary>
        int GetStableHashCode();
    }
}
