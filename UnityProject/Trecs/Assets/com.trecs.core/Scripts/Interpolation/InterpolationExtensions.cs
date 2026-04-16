using System.Runtime.CompilerServices;

namespace Trecs
{
    /// <summary>
    /// Extension methods on <see cref="EntityInitializer"/> and <see cref="NativeEntityInitializer"/>
    /// for initializing an entity with interpolation components in a single call.
    /// </summary>
    public static class InterpolationExtensions
    {
        public static EntityInitializer SetInterpolated<T>(
            this EntityInitializer initializer,
            in T value
        )
            where T : unmanaged, IEntityComponent
        {
            return initializer
                .Set(value)
                .Set(new Interpolated<T>(value))
                .Set(new InterpolatedPrevious<T>(value));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NativeEntityInitializer SetInterpolated<T>(
            this NativeEntityInitializer initializer,
            in T value
        )
            where T : unmanaged, IEntityComponent
        {
            return initializer
                .Set(value)
                .Set(new Interpolated<T>(value))
                .Set(new InterpolatedPrevious<T>(value));
        }
    }
}
