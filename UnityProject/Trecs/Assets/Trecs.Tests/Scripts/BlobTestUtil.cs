namespace Trecs.Tests
{
    /// <summary>
    /// Test helpers bridging the old eager <c>SharedPtr.Alloc</c> / <c>NativeSharedPtr.Alloc</c>
    /// shape onto the register-then-acquire API: register a constant source (cache-layer, no
    /// gating) and immediately acquire a handle. The acquire step asserts heap-mutation
    /// permission, preserving the role-gating the old Alloc had.
    /// </summary>
    public static class BlobTestUtil
    {
        public static SharedPtr<T> AllocShared<T>(WorldAccessor world, BlobId id, T value)
            where T : class
        {
            SharedAnchor.Register(world, id, value);
            return SharedPtr.Acquire<T>(world, id);
        }

        public static NativeSharedPtr<T> AllocNativeShared<T>(
            WorldAccessor world,
            BlobId id,
            in T value
        )
            where T : unmanaged
        {
            NativeSharedAnchor.Register(world, id, in value);
            return NativeSharedPtr.Acquire<T>(world, id);
        }
    }
}
