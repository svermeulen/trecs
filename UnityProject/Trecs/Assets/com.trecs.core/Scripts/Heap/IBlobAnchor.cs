namespace Trecs
{
    /// <summary>
    /// Type-erased view of an anchor: a disposable host-side reference that keeps a blob
    /// allocation resident in the world's shared heap. Implemented by the anchor types
    /// (<see cref="SharedAnchor{T}"/>, <see cref="NativeSharedAnchor{T}"/>) — and only by them;
    /// the in-simulation pointer family (<see cref="SharedPtr{T}"/>, <see cref="NativeSharedPtr{T}"/>, …)
    /// is unrelated. Useful for holding anchors of mixed element types in one collection and
    /// releasing them together via <see cref="Dispose(WorldAccessor)"/>.
    /// </summary>
    public interface IBlobAnchor
    {
        void Dispose(WorldAccessor world);
    }
}
