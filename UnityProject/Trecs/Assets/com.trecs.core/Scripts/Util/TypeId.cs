using System;
using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Burst;

namespace Trecs
{
    public readonly struct TypeId : IEquatable<TypeId>
    {
        public readonly int Value;

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
            if (raw == 0)
            {
                raw = 1;
            }
            var id = new TypeId(raw);
#endif
            TypeIdReverseLookup.Register(type, id);
            return id;
        }

        public static Type ToType(TypeId id)
        {
            return TypeIdReverseLookup.GetTypeFromId(id);
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

            if (value == 0)
            {
                value = 1;
            }

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
