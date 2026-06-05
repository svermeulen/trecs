using System;
using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Burst;

namespace Trecs
{
    public readonly struct TypeId : IEquatable<TypeId>
    {
        public readonly int Value;

        /// <summary>
        /// The null / unset TypeId, equivalent to <c>default(TypeId)</c>. Real type ids are
        /// normalized to be non-zero at construction (see <see cref="FromType"/> and
        /// <see cref="TypeId{T}"/>), so a zero <see cref="Value"/> unambiguously means null.
        /// </summary>
        // Declared without an initializer (matching TypeIdSet.Null / TagSet.Null) so it stays
        // a plain zero-initialized static readonly field — Burst-friendly, no cctor work.
        public static readonly TypeId Null;

        public bool IsNull
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Value == 0;
        }

        public TypeId(int value)
        {
            Value = value;
        }

        public override readonly bool Equals(object obj)
        {
            return obj is TypeId other && Equals(other);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool Equals(TypeId other)
        {
            return Value == other.Value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override readonly int GetHashCode()
        {
            return Value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(TypeId c1, TypeId c2)
        {
            return c1.Equals(c2);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(TypeId c1, TypeId c2)
        {
            return !c1.Equals(c2);
        }

        public override readonly string ToString()
        {
            return Value.ToString();
        }

        public static TypeId FromType(Type type)
        {
#if TRECS_REQUIRE_EXPLICIT_TYPE_IDS
            var id = new TypeId(ExplicitTypeIdProvider.GetTypeId(type));
#else
            var raw = BurstRuntime.GetHashCode32(type);
            // 0 is reserved for TypeId.Null. A type hashing to 0 is an astronomically rare
            // collision with the null id — reject it loudly (consistent with how
            // TypeIdSetRegistry handles a set XORing to 0) rather than silently remapping
            // it onto another id's value.
            TrecsAssert.That(
                raw != 0,
                "Type {0} hashes to 0, which is reserved for TypeId.Null. Rename it (in default "
                    + "mode a type's id is the hash of its name), or give it an explicit [TypeId] "
                    + "under TRECS_REQUIRE_EXPLICIT_TYPE_IDS.",
                type
            );
            var id = new TypeId(raw);
#endif
            TypeIdReverseLookup.Register(type, id);
            return id;
        }

        public static Type ToType(TypeId id)
        {
            return TypeIdReverseLookup.GetTypeFromId(id);
        }

        /// <summary>
        /// Non-throwing <see cref="ToType"/>: resolves <paramref name="id"/> to its registered
        /// <see cref="Type"/>, returning false if no type has been registered under it (rather than
        /// throwing). For callers that resolve ids from persisted data and want to report a missing
        /// registration with their own context.
        /// </summary>
        public static bool TryToType(TypeId id, out Type type)
        {
            return TypeIdReverseLookup.TryGetTypeFromId(id, out type);
        }

        public static void Register(Type type)
        {
            FromType(type);
        }

        public static void Register<T>()
        {
            Register(typeof(T));
        }

        public static bool IsRegistered(Type type)
        {
            return TypeIdReverseLookup.IsRegistered(type);
        }

#if TRECS_REQUIRE_EXPLICIT_TYPE_IDS
        // Pin an explicit ID for a type that can't carry a [TypeId] attribute (Unity types,
        // types in external assemblies). Only meaningful under TRECS_REQUIRE_EXPLICIT_TYPE_IDS;
        // not defined in default mode (call sites must be wrapped in the same #if).
        public static void RegisterExplicit<T>(int id) => RegisterExplicit(typeof(T), id);

        public static void RegisterExplicit(Type type, int id)
        {
            ExplicitTypeIdProvider.Register(type, id);
            TypeIdReverseLookup.Register(type, new TypeId(id));
        }
#endif
    }

    public static class TypeId<T>
    {
#if TRECS_REQUIRE_EXPLICIT_TYPE_IDS
        struct Key { }

        static readonly SharedStaticWrapper<int, Key> _value;

        public static TypeId Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                var value = _value.Data;
                TrecsAssert.That(
                    value != 0,
                    "TypeId<T>.Value accessed from Burst before the managed-side static ctor ran"
                );
                return new TypeId(value);
            }
        }

        static TypeId()
        {
            Init();
        }

        public static void Warmup() { }

        [BurstDiscard]
        static void Init()
        {
            if (_value.Data != 0)
            {
                return;
            }

            var id = ExplicitTypeIdProvider.GetTypeId(typeof(T));
            _value.Data = id;
            TypeIdReverseLookup.Register(typeof(T), new TypeId(id));
        }
#else
        // The static ctor body is Burst-AOT-evaluable in default mode (BurstRuntime.GetHashCode32
        // is a Burst intrinsic; the [BurstDiscard] RegisterReverseLookup runs only managed-side
        // for its side effect). Burst sees _value as a compile-time constant, so unlike
        // strict mode / Tag<T> / SetId<T> there's no warmup-before-Burst-read hazard here.
        static readonly TypeId _value;

        public static TypeId Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _value;
        }

        static TypeId()
        {
            var value = BurstRuntime.GetHashCode32<T>();

            // 0 is reserved for TypeId.Null — reject a type that hashes to it (see FromType).
            // TrecsAssert's managed throw is BurstDiscard'd, and for every real (non-zero)
            // type the condition is a compile-time-true constant, so Burst folds this away
            // and _value still constant-folds exactly as before.
            TrecsAssert.That(
                value != 0,
                "A type hashes to 0, which is reserved for TypeId.Null. Rename it (in default "
                    + "mode a type's id is the hash of its name), or give it an explicit [TypeId] "
                    + "under TRECS_REQUIRE_EXPLICIT_TYPE_IDS."
            );

            _value = new(value);

            RegisterReverseLookup();
        }

        public static void Warmup() { }

        [BurstDiscard]
        static void RegisterReverseLookup()
        {
            TypeIdReverseLookup.Register(typeof(T), _value);
        }
#endif
    }
}
