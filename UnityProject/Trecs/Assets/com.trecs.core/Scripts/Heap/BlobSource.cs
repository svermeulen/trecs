using System;

namespace Trecs.Internal
{
    /// <summary>
    /// The resident representation produced by <see cref="IBlobSource.Materialize"/>: the stored
    /// object (the managed object itself, or a <see cref="NativeBlobBox"/> for a native blob)
    /// paired with its in-memory byte size (<c>0</c> for managed — see
    /// <see cref="BlobMetadata.NativeBytes"/>). Carrying the size here lets the cache record it
    /// without reaching into the native-only <see cref="NativeBlobBox"/> on the shared resolve path.
    /// </summary>
    internal readonly struct MaterializedBlob
    {
        public readonly object Value;

        /// <summary>
        /// In-memory size of the native payload, in bytes (<see cref="NativeBlobBox.Size"/>).
        /// Always <c>0</c> for managed blobs — the byte cost of a managed object is not knowable
        /// in C#, so managed blobs are budgeted by count rather than bytes. Mirrors
        /// <see cref="BlobMetadata.NativeBytes"/>, which this value is recorded into.
        /// </summary>
        public readonly long NativeBytes;

        public MaterializedBlob(object value, long nativeBytes)
        {
            Value = value;
            NativeBytes = nativeBytes;
        }
    }

    /// <summary>
    /// Describes how the bytes for a registered <see cref="BlobId"/> are (re)materialized
    /// on demand. A source is registered once (see <c>BlobCache.RegisterManagedBlob</c> /
    /// <c>RegisterNativeBlob</c>); the <see cref="BlobCache"/> calls <see cref="Materialize"/>
    /// the first time the blob is accessed and again after any eviction. The source entry is
    /// never forgotten while the id stays registered, which is what makes every blob uniformly
    /// re-creatable.
    /// <para>
    /// <b>Determinism contract:</b> a source must be a pure function of inputs captured at
    /// registration time — it must not read mutable world state. Otherwise an evict→re-materialize
    /// can produce different bytes across machines/runs and silently desync the simulation.
    /// </para>
    /// <para>
    /// Native sources own the <see cref="NativeBlobBoxPool"/> they rent boxes from (captured at
    /// construction); managed sources have no such dependency. That asymmetry is kept inside the
    /// implementations so <see cref="Materialize"/> is uniform and pool-free.
    /// </para>
    /// </summary>
    internal interface IBlobSource
    {
        TypeId TypeId { get; }
        bool IsNative { get; }

        /// <summary>
        /// Produces the resident representation of the blob — the managed object itself for a
        /// managed blob, or a freshly-rented <see cref="NativeBlobBox"/> (owning native memory)
        /// for a native blob — together with its byte size.
        /// </summary>
        MaterializedBlob Materialize();
    }

    internal sealed class ManagedBlobSource<T> : IBlobSource
        where T : class
    {
        readonly Func<T> _factory;

        public ManagedBlobSource(Func<T> factory)
        {
            _factory = factory;
        }

        public TypeId TypeId => TypeId<T>.Value;
        public bool IsNative => false;

        public MaterializedBlob Materialize()
        {
            var value = _factory();
            TrecsAssert.That(
                value != null,
                "Managed blob factory for type {0} returned null",
                typeof(T)
            );
            return new MaterializedBlob(value, nativeBytes: 0);
        }
    }

    internal sealed class NativeBlobSource<T> : IBlobSource
        where T : unmanaged
    {
        readonly Func<T> _factory;
        readonly NativeBlobBoxPool _pool;

        public NativeBlobSource(Func<T> factory, NativeBlobBoxPool pool)
        {
            _factory = factory;
            _pool = pool;
        }

        public TypeId TypeId => TypeId<T>.Value;
        public bool IsNative => true;

        public MaterializedBlob Materialize()
        {
            var value = _factory();
            var box = _pool.RentFromValue(in value);
            return new MaterializedBlob(box, box.Size);
        }
    }

    internal sealed class NativeOwnershipBlobSource<T> : IBlobSource
        where T : unmanaged
    {
        readonly Func<NativeBlobAllocation> _factory;
        readonly NativeBlobBoxPool _pool;

        public NativeOwnershipBlobSource(Func<NativeBlobAllocation> factory, NativeBlobBoxPool pool)
        {
            _factory = factory;
            _pool = pool;
        }

        public TypeId TypeId => TypeId<T>.Value;
        public bool IsNative => true;

        public MaterializedBlob Materialize()
        {
            var alloc = _factory();
            var box = _pool.RentTakingOwnership(alloc, typeof(T));
            return new MaterializedBlob(box, box.Size);
        }
    }

    // ─── Descriptor-bound sources (BlobFactory) ────────────────────────────
    //
    // These back blobs registered through the BlobFactory: identity is hash(descriptor)
    // and the bytes are (re)produced by running a per-descriptor-type builder against the
    // captured descriptor. The builder delegate is registered once per descriptor type and
    // shared by every id of that type (held by reference here), so a fresh intern allocates
    // only this small source object holding the descriptor value — never a per-call closure.

    internal sealed class DescriptorManagedBlobSource<TDesc, T> : IBlobSource
        where T : class
    {
        readonly TDesc _descriptor;
        readonly Func<TDesc, T> _builder;

        public DescriptorManagedBlobSource(in TDesc descriptor, Func<TDesc, T> builder)
        {
            _descriptor = descriptor;
            _builder = builder;
        }

        public TypeId TypeId => TypeId<T>.Value;
        public bool IsNative => false;

        public MaterializedBlob Materialize()
        {
            var value = _builder(_descriptor);
            TrecsAssert.That(
                value != null,
                "Managed blob builder for descriptor type {0} returned null",
                typeof(TDesc)
            );
            return new MaterializedBlob(value, nativeBytes: 0);
        }
    }

    internal sealed class DescriptorNativeBlobSource<TDesc, T> : IBlobSource
        where T : unmanaged
    {
        readonly TDesc _descriptor;
        readonly Func<TDesc, T> _builder;
        readonly NativeBlobBoxPool _pool;

        public DescriptorNativeBlobSource(
            in TDesc descriptor,
            Func<TDesc, T> builder,
            NativeBlobBoxPool pool
        )
        {
            _descriptor = descriptor;
            _builder = builder;
            _pool = pool;
        }

        public TypeId TypeId => TypeId<T>.Value;
        public bool IsNative => true;

        public MaterializedBlob Materialize()
        {
            var value = _builder(_descriptor);
            var box = _pool.RentFromValue(in value);
            return new MaterializedBlob(box, box.Size);
        }
    }

    internal sealed class DescriptorNativeOwnershipBlobSource<TDesc, T> : IBlobSource
        where T : unmanaged
    {
        readonly TDesc _descriptor;
        readonly Func<TDesc, NativeBlobAllocation> _builder;
        readonly NativeBlobBoxPool _pool;

        public DescriptorNativeOwnershipBlobSource(
            in TDesc descriptor,
            Func<TDesc, NativeBlobAllocation> builder,
            NativeBlobBoxPool pool
        )
        {
            _descriptor = descriptor;
            _builder = builder;
            _pool = pool;
        }

        public TypeId TypeId => TypeId<T>.Value;
        public bool IsNative => true;

        public MaterializedBlob Materialize()
        {
            var alloc = _builder(_descriptor);
            var box = _pool.RentTakingOwnership(alloc, typeof(T));
            return new MaterializedBlob(box, box.Size);
        }
    }
}
