using System.Runtime.CompilerServices;

namespace Trecs
{
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
