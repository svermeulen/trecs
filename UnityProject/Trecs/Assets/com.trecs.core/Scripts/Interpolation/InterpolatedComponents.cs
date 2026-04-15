namespace Trecs
{
    public interface ICInterpolatedPrevious { }

    [Unwrap]
    [TypeId(389218230)]
    public partial struct InterpolatedPrevious<T> : IEntityComponent, ICInterpolatedPrevious
        where T : unmanaged, IEntityComponent
    {
        public T Value;

        public InterpolatedPrevious(T value)
        {
            Value = value;
        }
    }

    public interface ICInterpolated { }

    [Unwrap]
    [TypeId(311176127)]
    public partial struct Interpolated<T> : IEntityComponent, ICInterpolated
        where T : unmanaged, IEntityComponent
    {
        public T Value;

        public Interpolated(T value)
        {
            Value = value;
        }
    }
}
