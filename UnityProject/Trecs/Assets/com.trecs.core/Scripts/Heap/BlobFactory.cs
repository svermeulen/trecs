using System;
using System.Collections.Generic;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// The registration + materialization authority that sits above the <see cref="BlobCache"/>.
    /// Owns the per-world blob <b>source</b> registry (how each <see cref="BlobId"/> is built or
    /// rebuilt), the per-descriptor-type builder registry, the descriptor journal, and the shared
    /// <see cref="UniqueHashGenerator"/>. Orchestrates <b>acquire</b>: on a cache miss it
    /// materializes from the source and inserts the bytes into the cache, then the caller pins via
    /// <see cref="BlobCache.CreateHandle"/> — the cache itself holds only resident bytes and never
    /// calls back up here. Reached through <c>world.BlobFactory</c>.
    /// <para>
    /// Three ways a blob gets a source: a direct <c>Register*</c> (factory / eager value /
    /// taking-ownership), a <b>derivable</b> blob interned from a small descriptor (a sphere radius,
    /// a heightmap's parameters, …) which hashes to a stable content id and registers on first
    /// sight, or an out-of-core <b>opaque</b> blob registered through the OpaqueBlobLoader.
    /// </para>
    /// <para>
    /// The <i>public</i> surface lives on the pointer types, mirroring the rest of the heap family:
    /// register with the <c>SharedAnchor.Register</c> / <c>NativeSharedAnchor.Register</c> overloads (the
    /// descriptor-taking ones take a per-type builder; the native ones add a taking-ownership form
    /// for variable-sized blobs), then acquire — by id via
    /// <see cref="SharedPtr.Acquire{T}(WorldAccessor, BlobId)"/>, or straight from a descriptor via
    /// <see cref="SharedPtr.Acquire{TDesc,T}(WorldAccessor, in TDesc)"/> /
    /// <see cref="NativeSharedPtr.Acquire{TDesc,T}(WorldAccessor, in TDesc)"/>.
    /// </para>
    /// </summary>
    internal sealed class BlobFactory : IDisposable
    {
        readonly TrecsLog _log;
        readonly BlobCache _cache;
        readonly UniqueHashGenerator _hashGenerator;

        // The source registry: how each registered blob id is (re-)materialized. Add-only — a blob
        // is registered once and its source kept for the world's lifetime, so an evicted blob can be
        // transparently rebuilt on re-acquire. The BlobCache below holds only the resident bytes; it
        // has no knowledge of sources.
        readonly IterableDictionary<BlobId, IBlobSource> _sources = new();

        // The static tier: ids registered through the explicit Register* surface (and the
        // OpaqueBlobLoader), which program setup re-runs every run. Distinct from descriptor-
        // interned sources, whose fresh-process restorability rides the journal instead — the
        // save-time completeness check in CollectJournaledDescriptors needs to tell them apart.
        readonly HashSet<BlobId> _staticSources = new();

        // Sources registered from an *ambient* (non-deterministic) context: a descriptor interned
        // through a Variable-role anchor acquire, or restored from a recording's input stream. The
        // deterministic by-id resolve (ContainsManagedBlobDeterministic / ContainsNativeBlobDeterministic)
        // treats these as absent — whether ambient code happened to intern a descriptor must not
        // change what simulation code can resolve, or replay diverges from capture (works with
        // rendering attached, fails headless). A deterministic-context intern of the same descriptor
        // *promotes* the source (removes it from this set): the simulation has now justified the id
        // at a reproducible timeline point. Never downgraded. Absent from this set = deterministic:
        // setup-window Register* calls and gated (CanMutateHeap-passing) interns.
        readonly HashSet<BlobId> _ambientSources = new();

        // Blob ids whose source is mid-Materialize. Guards against a source factory re-entering to
        // resolve the same blob it is still building — without it that recurses forever (or, if it
        // terminated, double-inserts into the cache and orphans a rented NativeBlobBox).
        readonly HashSet<BlobId> _materializing = new();

        // Keyed by descriptor Type; value is a DescriptorBlobFactory<TDesc> (type-erased so the
        // dictionary can hold every descriptor type). Populated once at setup.
        readonly Dictionary<Type, object> _factories = new();

        // The descriptor journal: id -> boxed descriptor, recorded for every interned blob. A
        // snapshot persists the subset of this for the blobs it references
        // (see CollectJournaledDescriptors) so they can be re-derived on a fresh-process load (see
        // RestoreJournaledDescriptors). The boxing is one small allocation per distinct persistent
        // intern — negligible, and only on the registering (miss) path.
        readonly Dictionary<BlobId, object> _journal = new();

#if DEBUG
        // Debug-only collision guard: the descriptor type that first produced each id. Catches the
        // realistic structural bug — two different descriptor types hashing to the same blob id
        // (e.g. a counter-based id reused across subsystems) — turning a silent determinism desync
        // into an assert. A true within-type 64-bit content collision is astronomically unlikely
        // and not worth re-hashing for.
        readonly Dictionary<BlobId, Type> _internedDescriptorTypes = new();
#endif

        // Ids whose source entered through the *input* pipeline (a descriptor-based input acquire,
        // or a recording's input stream) and which the sim has not yet justified. Unlike sim-side
        // interns — whose descriptor space is bounded by a fixed setup-time catalogue — input
        // descriptors can come from a wide or unbounded space over a long session, so their
        // source/journal entries get a real lifetime: SweepInputDescriptors forgets each one once
        // no live input frame references it and it was never promoted. Promoted ids leave this set
        // and become permanent, like any sim intern.
        readonly HashSet<BlobId> _inputDescriptorIds = new();
        readonly List<BlobId> _inputDescriptorSweepBuffer = new();
        int _inputDescriptorChurnSinceSweep;

        // How much input-descriptor churn — newly tracked ids plus released descriptor-carrying
        // heap entries (releases are what turn tracked ids into garbage) — must accumulate before
        // the next sweep is requested. Churn-triggered (not per-trim) so steady-state games with a
        // small/bounded input descriptor space never pay for a sweep at all; stale entries are
        // harmless in the interim (snapshot emission filters by the referenced set, and ambient
        // sources are invisible to the deterministic resolve) — only their memory matters, which
        // this bounds.
        internal const int InputDescriptorSweepChurn = 64;

        bool _isDisposed;

        public BlobFactory(TrecsLog log, BlobCache cache, SerializerRegistry serializerRegistry)
        {
            _log = log;
            _cache = cache;
            _hashGenerator = new UniqueHashGenerator(serializerRegistry);
        }

        // The factory is main-thread-only, like the cache it sits over. Asserting here covers the
        // source-registry-only paths (registration, the not-resident gate branch, IsRegistered)
        // that don't otherwise route through a cache call.
        void AssertMainThreadAndNotDisposed()
        {
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.That(
                UnityThreadHelper.IsMainThread,
                "BlobFactory is main-thread only"
            );
        }

        // Exposed so native sources/descriptor factories can hand the per-world pool to the native
        // boxes they rent — the pool itself lives on the cache.
        internal NativeBlobBoxPool NativeBlobBoxPool => _cache.NativeBlobBoxPool;

        // ─── Source registration ────────────────────────────────────────

        /// <summary>
        /// Registers a managed blob under <paramref name="id"/> with a factory that builds it on
        /// first access. Does not materialize or pin — acquire a handle separately. Asserts the id
        /// is not already registered.
        /// </summary>
        public void RegisterManagedBlob<T>(BlobId id, Func<T> factory)
            where T : class
        {
            TrecsAssert.That(factory != null, "factory must not be null");
            RegisterSource(id, new ManagedBlobSource<T>(factory), isStaticRegistration: true);
        }

        /// <summary>
        /// Registers a managed blob under <paramref name="id"/> from an eager value — sugar for a
        /// constant factory.
        /// </summary>
        public void RegisterManagedBlob<T>(BlobId id, T value)
            where T : class
        {
            TrecsAssert.That(value != null, "value must not be null");
            RegisterSource(id, new ManagedBlobSource<T>(() => value), isStaticRegistration: true);
        }

        /// <summary>
        /// Registers a native blob under <paramref name="id"/> with a factory that builds it on
        /// first access.
        /// </summary>
        public void RegisterNativeBlob<T>(BlobId id, Func<T> factory)
            where T : unmanaged
        {
            TrecsAssert.That(factory != null, "factory must not be null");
            RegisterSource(
                id,
                new NativeBlobSource<T>(factory, NativeBlobBoxPool),
                isStaticRegistration: true
            );
        }

        /// <summary>
        /// Registers a native blob under <paramref name="id"/> from an eager value — sugar for a
        /// constant factory.
        /// </summary>
        public void RegisterNativeBlob<T>(BlobId id, in T value)
            where T : unmanaged
        {
            var captured = value;
            RegisterSource(
                id,
                new NativeBlobSource<T>(() => captured, NativeBlobBoxPool),
                isStaticRegistration: true
            );
        }

        /// <summary>
        /// Registers a native blob whose factory hands ownership of a pre-allocated native buffer to
        /// the cache. The factory must allocate a fresh buffer per call (it may run again after
        /// eviction). For variable-sized types where the allocation is larger than sizeof(T).
        /// </summary>
        public void RegisterNativeBlobTakingOwnership<T>(
            BlobId id,
            Func<NativeBlobAllocation> factory
        )
            where T : unmanaged
        {
            TrecsAssert.That(factory != null, "factory must not be null");
            RegisterSource(
                id,
                new NativeOwnershipBlobSource<T>(factory, NativeBlobBoxPool),
                isStaticRegistration: true
            );
        }

        /// <summary>
        /// <paramref name="isStaticRegistration"/> records which persistence tier the source
        /// belongs to: true for the static tier — explicit <c>Register*</c> calls (and the
        /// OpaqueBlobLoader), which program setup re-runs every run, so snapshots need no journal
        /// entry for them — false for descriptor-interned sources, whose snapshot restorability
        /// depends on a journal entry instead. The save-time completeness check
        /// (<see cref="CollectJournaledDescriptors"/>) uses the distinction.
        /// </summary>
        internal void RegisterSource(BlobId id, IBlobSource source, bool isStaticRegistration)
        {
            AssertMainThreadAndNotDisposed();
            TrecsAssert.That(
                !_sources.ContainsKey(id),
                "A blob source already exists under id {0}",
                id
            );
            _sources.Add(id, source);
            if (isStaticRegistration)
            {
                _staticSources.Add(id);
            }
            _log.Trace("Registered blob source for id {0}", id);
        }

        /// <summary>Returns true if a blob source is registered under <paramref name="id"/>.</summary>
        internal bool IsRegistered(BlobId id)
        {
            AssertMainThreadAndNotDisposed();
            return _sources.ContainsKey(id);
        }

        // ─── Acquire orchestration (materialize-on-miss, then pin) ──────

        /// <summary>
        /// Ensures the bytes for <paramref name="id"/> are resident, materializing from the
        /// registered source on a miss (first access, or re-access after eviction). The acquire-side
        /// counterpart to the cache's <see cref="BlobCache.CreateHandle"/> pin: heap acquire paths
        /// ensure residency through the factory, then pin via the cache. Returns false if no blob is
        /// registered at the id and it is not already resident.
        /// </summary>
        internal bool EnsureResident(BlobId id)
        {
            AssertMainThreadAndNotDisposed();

            if (_cache.IsResident(id))
            {
                return true;
            }

            if (!_sources.TryGetValue(id, out var source))
            {
                return false;
            }

            // Guard against a source re-entering to resolve the same id it is building. A nested
            // resolve of a *different* id is fine (e.g. a composite blob built from sub-blobs); only
            // same-id recursion is rejected. The source is user code and may re-enter (registering /
            // allocating other blobs), which is why the cache insert happens only after Materialize
            // returns.
            if (!_materializing.Add(id))
            {
                throw TrecsDebugAssert.CreateException(
                    "Recursive materialization of blob {0}: its source re-entered to resolve the "
                        + "same blob it is still building.",
                    id
                );
            }

            MaterializedBlob materialized;
            try
            {
                materialized = source.Materialize();
            }
            finally
            {
                _materializing.Remove(id);
            }

            try
            {
                _cache.Insert(id, materialized.Value, source.TypeId, materialized.NativeBytes);
            }
            catch
            {
                // The insert can throw in debug builds (content attestation / id-fingerprint
                // asserts); release the freshly-rented native box on that path instead of leaking
                // it — mirrors the guard the cache-level Alloc* paths already have.
                (materialized.Value as NativeBlobBox)?.Dispose();
                throw;
            }
            return true;
        }

        /// <summary>
        /// <b>Ambient</b> containment: returns true if a managed blob is registered or resident at
        /// <paramref name="id"/> whose declared type is assignable to <typeparamref name="T"/>
        /// (interfaces and base classes match). Consults raw cache residency, so the answer can vary
        /// with eviction timing and who happens to hold pins — fine for the anchor layer (where "is
        /// it here right now?" is the intended question, e.g. async-readiness polling), <b>never</b>
        /// for a deterministic resolve: see <see cref="ContainsManagedBlobDeterministic{T}"/>.
        /// Asymmetric with <see cref="ContainsNativeBlob{T}"/>, which requires exact equality.
        /// </summary>
        internal bool ContainsManagedBlob<T>(BlobId id)
            where T : class
        {
            AssertMainThreadAndNotDisposed();
            if (_cache.TryGetResidentTypeInfo(id, out var typeId, out var isNative))
            {
                return !isNative && typeof(T).IsAssignableFrom(TypeId.ToType(typeId));
            }
            if (_sources.TryGetValue(id, out var source))
            {
                return !source.IsNative && typeof(T).IsAssignableFrom(TypeId.ToType(source.TypeId));
            }
            return false;
        }

        /// <summary>
        /// <b>Deterministic</b> containment, for the gated (simulation) by-id resolve: true iff a
        /// <i>deterministically registered</i> source exists at <paramref name="id"/> with a
        /// compatible managed type. Never consults cache residency (the cache is not simulation
        /// state) and treats ambient-tagged sources as absent — so the answer is identical in every
        /// run of the same timeline, resident or not, rendering attached or headless.
        /// </summary>
        internal bool ContainsManagedBlobDeterministic<T>(BlobId id)
            where T : class
        {
            AssertMainThreadAndNotDisposed();
            if (!_sources.TryGetValue(id, out var source) || _ambientSources.Contains(id))
            {
                return false;
            }
            return !source.IsNative && typeof(T).IsAssignableFrom(TypeId.ToType(source.TypeId));
        }

        /// <summary>
        /// <b>Ambient</b> containment: returns true if a native blob is registered or resident at
        /// <paramref name="id"/> whose declared type is <i>exactly</i> <typeparamref name="T"/>.
        /// See <see cref="ContainsManagedBlob{T}"/> for the ambient-vs-deterministic split.
        /// </summary>
        internal bool ContainsNativeBlob<T>(BlobId id)
            where T : unmanaged
        {
            AssertMainThreadAndNotDisposed();
            if (_cache.TryGetResidentTypeInfo(id, out var typeId, out var isNative))
            {
                return isNative && typeId == TypeId<T>.Value;
            }
            if (_sources.TryGetValue(id, out var source))
            {
                return source.IsNative && source.TypeId == TypeId<T>.Value;
            }
            return false;
        }

        /// <summary>
        /// <b>Deterministic</b> containment for native blobs — the exact-type counterpart to
        /// <see cref="ContainsManagedBlobDeterministic{T}"/>.
        /// </summary>
        internal bool ContainsNativeBlobDeterministic<T>(BlobId id)
            where T : unmanaged
        {
            AssertMainThreadAndNotDisposed();
            if (!_sources.TryGetValue(id, out var source) || _ambientSources.Contains(id))
            {
                return false;
            }
            return source.IsNative && source.TypeId == TypeId<T>.Value;
        }

        /// <summary>
        /// Returns a fresh pinning <see cref="SharedAnchor{T}"/> for the managed blob registered at
        /// <paramref name="blobId"/>, throwing if none is registered. Materializes the blob eagerly
        /// (so it is resident before the returned pointer is read) and pins it.
        /// </summary>
        public SharedAnchor<T> AcquireSharedAnchor<T>(BlobId blobId)
            where T : class
        {
            if (!TryAcquireSharedAnchor<T>(blobId, out var ptr))
            {
                throw TrecsDebugAssert.CreateException(
                    "Attempted to acquire ptr for unregistered managed blob {0}",
                    blobId
                );
            }
            return ptr;
        }

        public bool TryAcquireSharedAnchor<T>(BlobId blobId, out SharedAnchor<T> ptr)
            where T : class
        {
            if (!ContainsManagedBlob<T>(blobId))
            {
                ptr = default;
                return false;
            }
            EnsureResident(blobId);
            ptr = new SharedAnchor<T>(_cache.CreateHandle(blobId), blobId);
            return true;
        }

        public NativeSharedAnchor<T> AcquireNativeSharedAnchor<T>(BlobId blobId)
            where T : unmanaged
        {
            if (!TryAcquireNativeSharedAnchor<T>(blobId, out var ptr))
            {
                throw TrecsDebugAssert.CreateException(
                    "Attempted to acquire ptr for unregistered native blob {0}",
                    blobId
                );
            }
            return ptr;
        }

        public bool TryAcquireNativeSharedAnchor<T>(BlobId blobId, out NativeSharedAnchor<T> ptr)
            where T : unmanaged
        {
            if (!ContainsNativeBlob<T>(blobId))
            {
                ptr = default;
                return false;
            }
            EnsureResident(blobId);
            ptr = new NativeSharedAnchor<T>(_cache.CreateHandle(blobId), blobId);
            return true;
        }

        public void AddFactory<TDesc>(DescriptorBlobFactory<TDesc> factory)
        {
            AssertMainThreadAndNotDisposed();
            TrecsAssert.That(factory != null, "factory must not be null");
            TrecsAssert.That(
                !_factories.ContainsKey(typeof(TDesc)),
                "A blob factory is already registered for descriptor type {0}",
                typeof(TDesc)
            );
            _factories.Add(typeof(TDesc), factory);
            _log.Trace("Registered blob factory for descriptor type {0}", typeof(TDesc));
        }

        /// <summary>
        /// Hashes <paramref name="descriptor"/> to its content-addressed id, registers the blob's
        /// source on first sight, and journals the descriptor.
        /// <paramref name="deterministicContext"/> tags the registered source: true (the default)
        /// for gated callers — interns reached through a CanMutateHeap-passing accessor, which
        /// replay re-executes at the same timeline point — false for ambient callers (Variable-role
        /// anchor acquires, the input-pointer path), whose sources stay invisible to the
        /// deterministic resolve. A deterministic re-intern of an ambient-registered descriptor
        /// promotes it (see <c>_ambientSources</c>).
        /// <para>
        /// The journal write is unconditional on context: the journal is <i>knowledge</i> (how to
        /// rebuild an id), the tag is <i>justification</i> (whether sim may resolve it) — orthogonal
        /// facts. Journaling ambient/input interns leaks nothing into snapshots, because snapshot
        /// emission filters the journal by the heap-derived referenced set; what it buys is that any
        /// blob the simulation later comes to hold (by-id resolve after promotion, or the input→sim
        /// pointer conversion) has its recipe on record — including blobs whose only other
        /// descriptor copy (an input-heap frame entry) has since been trimmed.
        /// </para>
        /// </summary>
        public BlobId Intern<TDesc>(in TDesc descriptor, bool deterministicContext = true)
        {
            AssertMainThreadAndNotDisposed();

            var hash = _hashGenerator.Generate(in descriptor);
            var id = new BlobId(hash);

#if DEBUG
            VerifyNoCollision<TDesc>(id);
#endif

            if (!_sources.ContainsKey(id))
            {
                if (!_factories.TryGetValue(typeof(TDesc), out var factoryObj))
                {
                    throw TrecsDebugAssert.CreateException(
                        "No blob factory registered for descriptor type {0}. Register one at setup "
                            + "via the descriptor-taking SharedAnchor.Register<{0}, T>(...) / "
                            + "NativeSharedAnchor.Register<{0}, T>(...) overload.",
                        typeof(TDesc)
                    );
                }

                var factory = (DescriptorBlobFactory<TDesc>)factoryObj;
                factory.Register(this, id, in descriptor);
                if (!deterministicContext)
                {
                    _ambientSources.Add(id);
                }
                _log.Trace("Interned new blob {0} from descriptor type {1}", id, typeof(TDesc));
            }
            else if (deterministicContext)
            {
                // Promotion: the simulation (or another deterministic context) has now justified
                // this id at a reproducible timeline point, so an ambient-registered source becomes
                // visible to the deterministic resolve. Never downgraded.
                _ambientSources.Remove(id);
            }

            // Record the descriptor so a snapshot can re-derive the blob on a fresh-process load.
            // A swept input descriptor (see SweepInputDescriptors) loses journal entry and source
            // together, so a re-intern lands on the registering branch above and re-journals there;
            // this guarded write covers the one remaining hit-path gap — an id whose source exists
            // without a journal entry (a static Register* id that a descriptor also hashes to) —
            // and keeps the steady-state re-intern path off the dict write.
            if (!_journal.ContainsKey(id))
            {
                _journal[id] = descriptor;
            }

            return id;
        }

        // ─── Content-addressed eager managed alloc ──────────────────────────

        /// <summary>
        /// Derives a content-addressed <see cref="BlobId"/> for an opaque managed <paramref name="blob"/>
        /// by serializing it and hashing the bytes, and makes it resident eager (no pin) on a miss
        /// (dedup on a hit). Returns the id; the caller pins. This is the managed counterpart to the
        /// native byte-hash content-addressing, and the cost (a full serialize) is the price of an
        /// opaque managed blob — blobs that <i>can</i> be described should use a descriptor builder
        /// (which hashes the small descriptor, not the whole blob) and acquire via the descriptor
        /// <c>Acquire</c> overload instead.
        /// </summary>
        internal BlobId AllocManagedContentAddressed<T>(T blob)
            where T : class
        {
            AssertMainThreadAndNotDisposed();
            TrecsAssert.That(blob != null, "blob must not be null");

            var id = new BlobId(_hashGenerator.Generate(blob));
            if (!_cache.IsResident(id))
            {
                _cache.InsertEagerBlob(id, blob);
            }
            return id;
        }

        /// <summary>
        /// Derives a content-addressed <see cref="BlobId"/> for <paramref name="value"/> by serializing
        /// and hashing it — no allocation, no storage. The shared primitive behind
        /// <see cref="BlobIdGenerator.FromContent{T}(WorldAccessor, in T)"/>: callers that want a
        /// content-addressed blob hash the value to an id, check residency, build on a miss, and store
        /// it with <c>Alloc(world, id, ...)</c>. <typeparamref name="T"/> must be registered for
        /// serialization.
        /// </summary>
        internal BlobId DeriveContentId<T>(in T value)
        {
            AssertMainThreadAndNotDisposed();
            return new BlobId(_hashGenerator.Generate(in value));
        }

        // ─── Descriptor journal (snapshot re-derivation) ────────────────────

        /// <summary>
        /// Returns the journal's boxed copy of the descriptor that hashed to <paramref name="id"/>.
        /// Only valid immediately after an <see cref="Intern{TDesc}"/> of that descriptor — Intern's
        /// postcondition is that the journal contains the id (nothing can prune in between; all
        /// main-thread). Callers that need an <c>object</c> copy of a just-interned struct
        /// descriptor (the input heaps' frame entries) share this box instead of boxing again,
        /// keeping steady-state re-interns allocation-free.
        /// </summary>
        internal object GetJournaledDescriptor(BlobId id)
        {
            AssertMainThreadAndNotDisposed();
            _journal.TryGetValue(id, out var descriptor);
            TrecsDebugAssert.That(
                descriptor != null,
                "No journal entry for blob {0} — GetJournaledDescriptor must only be called "
                    + "immediately after an Intern of the descriptor that produced the id",
                id
            );
            return descriptor;
        }

        /// <summary>
        /// Copies the journaled descriptors for the given active blob ids into
        /// <paramref name="output"/>, iterating <paramref name="activeIds"/> so the result order is
        /// deterministic (the wire format must be stable for checksums). Called at snapshot time.
        /// </summary>
        public void CollectJournaledDescriptors(
            IterableHashSet<BlobId> activeIds,
            IterableDictionary<BlobId, object> output
        )
        {
            AssertMainThreadAndNotDisposed();
#if !DEBUG || TRECS_IS_PROFILING
            // An empty journal can collect nothing — skip the per-id probes (paid per save on the
            // rollback hot path; worlds whose blobs are all statically registered never journal).
            // DEBUG builds keep the walk: the else-branch below is the save-time restorability
            // backstop, which is exactly as meaningful when the journal is empty. (Profiling
            // builds skip even with DEBUG, so editor-backend bench numbers see release behavior.)
            if (_journal.Count == 0)
            {
                return;
            }
#endif
            foreach (var id in activeIds)
            {
                if (_journal.TryGetValue(id, out var descriptor))
                {
                    output.Add(id, descriptor);
                }
#if DEBUG
                else
                {
                    AssertRestorableWithoutJournalEntry(id);
                }
#endif
            }
        }

#if DEBUG
        // Save-time completeness check: every snapshot-referenced blob must be restorable on a
        // fresh-process load through one of the three persistence channels — journal entry
        // (handled by the caller above), static registration (program setup re-registers it every
        // run), or eager bytes (the opaque-blob section persists them). A blob that is none of
        // these would snapshot as a bare id and fail only at load time, far from the bug. No known
        // path produces one (SweepInputDescriptors drops journal entry and source together, and
        // only for sim-unjustified ids the heaps can't reference) — pure backstop.
        void AssertRestorableWithoutJournalEntry(BlobId id)
        {
            if (_staticSources.Contains(id))
            {
                return;
            }
            var metadata = _cache.GetBlobMetadata(id);
            TrecsDebugAssert.That(
                metadata.IsEager,
                "Snapshot references blob {0}, which has no journal entry, is not a setup-registered "
                    + "(static) source, and is not eager — a fresh-process load could not restore it.",
                id
            );
        }
#endif

        /// <summary>
        /// Re-registers a blob source for each journaled descriptor, so the blob can be materialized
        /// once the heaps deserialize and pin it by id. Skips ids already registered (e.g. an
        /// in-session rollback where the blob was interned this session). Must run <i>before</i> the
        /// heaps deserialize.
        /// </summary>
        public void RestoreJournaledDescriptors(IterableDictionary<BlobId, object> journal)
        {
            AssertMainThreadAndNotDisposed();
            foreach (var (id, descriptor) in journal)
            {
                if (!_sources.ContainsKey(id))
                {
                    var type = descriptor.GetType();
                    TrecsAssert.That(
                        _factories.TryGetValue(type, out var factoryObj),
                        "No blob factory registered for journaled descriptor type {0}; register it "
                            + "at setup before loading a snapshot that references it.",
                        type
                    );
                    ((IDescriptorBlobFactory)factoryObj).RegisterBoxed(this, id, descriptor);
                }
                // A journaled descriptor is simulation-justified by definition (only sim-referenced
                // blobs enter the snapshot journal), so promote any pre-existing ambient source.
                _ambientSources.Remove(id);
                // Repopulate the live journal so a subsequent re-save of this state includes it.
                _journal[id] = descriptor;
            }
        }

        /// <summary>
        /// Re-registers the source for a single descriptor-acquired <i>input</i> blob read back from
        /// a recording's input stream, so the blob re-derives on replay. Idempotent: a no-op when a
        /// source is already registered (e.g. simulation state interned the same descriptor).
        /// </summary>
        internal void RestoreInputDescriptor(BlobId id, object descriptor)
        {
            AssertMainThreadAndNotDisposed();
            if (_sources.ContainsKey(id))
            {
                return;
            }
            var type = descriptor.GetType();
            TrecsAssert.That(
                _factories.TryGetValue(type, out var factoryObj),
                "No blob factory registered for input descriptor type {0}; register it at setup "
                    + "before loading a recording that references it.",
                type
            );
            ((IDescriptorBlobFactory)factoryObj).RegisterBoxed(this, id, descriptor);
            // Input-stream sources are ambient: the input heap delivers the blob to the sim through
            // in-hand frame payloads, never by-id resolve, so the source stays invisible to the
            // deterministic resolve until a deterministic context justifies it (intern or the
            // input→sim pointer conversion, both of which promote).
            _ambientSources.Add(id);
            // Journal the recipe regardless (knowledge vs justification — see Intern): if the sim
            // later converts this blob into its own heap, a subsequent snapshot must be able to
            // re-derive it even after the descriptor-carrying input frame is trimmed.
            if (!_journal.ContainsKey(id))
            {
                _journal[id] = descriptor;
            }
            // Input-pipeline origin: eligible for SweepInputDescriptors once its replayed frames
            // trim (the registering path only — on the early return above the id either came
            // through an input acquire this session, which already marked it, or is sim/static
            // owned and must stay permanent).
            MarkInputDescriptor(id);
        }

        /// <summary>
        /// Clears the ambient tag on <paramref name="id"/> (a no-op for ids with no source or no
        /// tag) at the moment simulation state deterministically justifies it — the input→sim
        /// pointer-conversion path (<see cref="SharedPtr.Acquire{T}(WorldAccessor, InputSharedPtr{T})"/>).
        /// Replay re-runs the conversion at the same timeline point, so the promotion is itself
        /// deterministic. The recipe needs no separate hand-off: every intern journals (see
        /// <see cref="Intern{TDesc}"/>), so the snapshot emission picks it up once the blob is
        /// sim-held.
        /// </summary>
        internal void PromoteToDeterministic(BlobId id)
        {
            AssertMainThreadAndNotDisposed();
            _ambientSources.Remove(id);
        }

        // ─── Input-descriptor sweep (bounded memory for input interns) ──────

        /// <summary>
        /// Tags <paramref name="id"/> as having entered through the input pipeline, making it a
        /// candidate for <see cref="SweepInputDescriptors"/>. Called by the input heaps on every
        /// descriptor acquire (idempotent) and by <see cref="RestoreInputDescriptor"/> on replay.
        /// </summary>
        internal void MarkInputDescriptor(BlobId id)
        {
            AssertMainThreadAndNotDisposed();
            if (_inputDescriptorIds.Add(id))
            {
                _inputDescriptorChurnSinceSweep += 1;
            }
        }

        // Counts a released descriptor-carrying input-heap entry toward the sweep trigger — a
        // tracked id can only become garbage when an entry referencing it goes away, so adds alone
        // would disarm after a sweep that found everything still live. A bare counter bump: called
        // from the heaps' bulk release paths, including during world teardown, so deliberately
        // assert-free.
        internal void NoteInputDescriptorEntryReleased()
        {
            _inputDescriptorChurnSinceSweep += 1;
        }

        // True once enough input-descriptor churn has accumulated since the last sweep to be
        // worth a pass (see InputDescriptorSweepChurn). Checked by EntityInputQueue on every
        // input-frame trim — one int compare in the common (not-due) case.
        internal bool InputDescriptorSweepDue =>
            _inputDescriptorChurnSinceSweep >= InputDescriptorSweepChurn;

        /// <summary>
        /// Forgets input-descriptor blobs that nothing input-side references anymore: every tracked
        /// id that is still ambient (the sim never justified it — a deterministic intern or the
        /// input→sim pointer conversion would have promoted it) and absent from
        /// <paramref name="liveInputIds"/> (the union of both input heaps' live frame entries) loses
        /// its source, journal entry, ambient tag, and resident bytes. Such an id is fully
        /// re-derivable — re-interning the same descriptor re-registers and re-journals — and the
        /// sim can never come to need it spontaneously, because ambient sources are invisible to
        /// the deterministic by-id resolve. Promoted ids just leave the tracked set: they are sim
        /// state now and their recipe must stay journaled for snapshots, like any sim intern.
        /// <para>
        /// Deferral-safe by construction: liveness is computed at sweep time, so running this
        /// late only keeps stale (harmless) entries longer — it can never drop a live one. The
        /// one observable narrowing is that a by-id acquire of a swept blob — from the input
        /// heaps or an ambient anchor — misses where the previously-immortal source would have
        /// satisfied it; descriptor blobs are recipes, so callers re-acquire by descriptor.
        /// </para>
        /// </summary>
        internal void SweepInputDescriptors(IterableHashSet<BlobId> liveInputIds)
        {
            AssertMainThreadAndNotDisposed();
            TrecsDebugAssert.That(_inputDescriptorSweepBuffer.IsEmpty());

            _inputDescriptorChurnSinceSweep = 0;

            int numSwept = 0;
            foreach (var id in _inputDescriptorIds)
            {
                if (!_ambientSources.Contains(id))
                {
                    // Promoted — permanent from here on; stop tracking it.
                    _inputDescriptorSweepBuffer.Add(id);
                    continue;
                }
                if (liveInputIds.Contains(id))
                {
                    continue;
                }

                var wasRemoved = _sources.TryRemove(id);
                TrecsDebugAssert.That(wasRemoved);
                _journal.Remove(id);
                _ambientSources.Remove(id);
#if DEBUG
                // Re-interning re-adds the collision-guard entry; dropping it here keeps DEBUG
                // builds bounded too, at the cost of not catching a collision against an id that
                // is currently swept-out.
                _internedDescriptorTypes.Remove(id);
#endif
                // Make the forget immediate rather than whenever the LRU happens to reach it — a
                // no-op if another ambient pin still holds the blob active (its bytes then age out
                // through the normal inactive caps) or if the bytes were already evicted.
                _cache.TryEvictInactive(id);

                _inputDescriptorSweepBuffer.Add(id);
                numSwept += 1;
            }

            foreach (var id in _inputDescriptorSweepBuffer)
            {
                _inputDescriptorIds.Remove(id);
            }
            _inputDescriptorSweepBuffer.Clear();

            if (numSwept > 0)
            {
                _log.Debug("Swept {0} unreferenced input descriptor blobs", numSwept);
            }
        }

#if DEBUG
        void VerifyNoCollision<TDesc>(BlobId id)
        {
            if (_internedDescriptorTypes.TryGetValue(id, out var existing))
            {
                TrecsDebugAssert.That(
                    existing == typeof(TDesc),
                    "BlobId collision: descriptor types {0} and {1} both hashed to blob id {2}. "
                        + "Content-addressed identity is broken — check for a non-content-derived "
                        + "(e.g. counter-based) id source.",
                    existing,
                    typeof(TDesc),
                    id
                );
            }
            else
            {
                _internedDescriptorTypes.Add(id, typeof(TDesc));
            }
        }
#endif

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }
            _isDisposed = true;
        }
    }

    // ─── Per-descriptor-type factories ──────────────────────────────────────
    //
    // Type-erased into BlobFactory's dictionary as object, downcast to
    // DescriptorBlobFactory<TDesc> in Intern. Each factory knows the concrete blob type T (which
    // the descriptor type alone does not encode) and constructs the matching descriptor-bound
    // IBlobSource. The builder delegate is captured once here and shared across every id.

    // Non-generic face of a factory so the registry can re-register a blob from a boxed descriptor
    // (whose TDesc is only known at runtime, on snapshot load).
    internal interface IDescriptorBlobFactory
    {
        void RegisterBoxed(BlobFactory factory, BlobId id, object descriptor);
    }

    internal abstract class DescriptorBlobFactory<TDesc> : IDescriptorBlobFactory
    {
        public abstract void Register(BlobFactory factory, BlobId id, in TDesc descriptor);

        public void RegisterBoxed(BlobFactory factory, BlobId id, object descriptor)
        {
            var typed = (TDesc)descriptor;
            Register(factory, id, in typed);
        }
    }

    internal sealed class ManagedDescriptorBlobFactory<TDesc, T> : DescriptorBlobFactory<TDesc>
        where T : class
    {
        readonly Func<TDesc, T> _builder;

        public ManagedDescriptorBlobFactory(Func<TDesc, T> builder)
        {
            TrecsAssert.That(builder != null, "builder must not be null");
            _builder = builder;
        }

        public override void Register(BlobFactory factory, BlobId id, in TDesc descriptor)
        {
            factory.RegisterSource(
                id,
                new DescriptorManagedBlobSource<TDesc, T>(in descriptor, _builder),
                isStaticRegistration: false
            );
        }
    }

    internal sealed class NativeDescriptorBlobFactory<TDesc, T> : DescriptorBlobFactory<TDesc>
        where T : unmanaged
    {
        readonly Func<TDesc, T> _builder;

        public NativeDescriptorBlobFactory(Func<TDesc, T> builder)
        {
            TrecsAssert.That(builder != null, "builder must not be null");
            _builder = builder;
        }

        public override void Register(BlobFactory factory, BlobId id, in TDesc descriptor)
        {
            factory.RegisterSource(
                id,
                new DescriptorNativeBlobSource<TDesc, T>(
                    in descriptor,
                    _builder,
                    factory.NativeBlobBoxPool
                ),
                isStaticRegistration: false
            );
        }
    }

    internal sealed class NativeOwnershipDescriptorBlobFactory<TDesc, T>
        : DescriptorBlobFactory<TDesc>
        where T : unmanaged
    {
        readonly Func<TDesc, NativeBlobAllocation> _builder;

        public NativeOwnershipDescriptorBlobFactory(Func<TDesc, NativeBlobAllocation> builder)
        {
            TrecsAssert.That(builder != null, "builder must not be null");
            _builder = builder;
        }

        public override void Register(BlobFactory factory, BlobId id, in TDesc descriptor)
        {
            factory.RegisterSource(
                id,
                new DescriptorNativeOwnershipBlobSource<TDesc, T>(
                    in descriptor,
                    _builder,
                    factory.NativeBlobBoxPool
                ),
                isStaticRegistration: false
            );
        }
    }
}
