namespace Trecs
{
    public interface IInterpolatedPrevious { }

    [Unwrap]
    [TypeId(389218230)]
    public partial struct InterpolatedPrevious<T> : IEntityComponent, IInterpolatedPrevious
        where T : unmanaged, IEntityComponent
    {
        public T Value;

        public InterpolatedPrevious(T value)
        {
            Value = value;
        }
    }

    public interface IInterpolated { }

    [Unwrap]
    [TypeId(311176127)]
    public partial struct Interpolated<T> : IEntityComponent, IInterpolated
        where T : unmanaged, IEntityComponent
    {
        public T Value;

        public Interpolated(T value)
        {
            Value = value;
        }
    }
}
