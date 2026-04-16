namespace Trecs
{
    /// <summary>
    /// Marker interface for all <see cref="InterpolatedPrevious{T}"/> component instances.
    /// </summary>
    public interface IInterpolatedPrevious { }

    /// <summary>
    /// Component that stores the previous-frame snapshot of <typeparamref name="T"/>,
    /// used by <see cref="InterpolatedUpdater{T}"/> to blend between frames.
    /// </summary>
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

    /// <summary>
    /// Marker interface for all <see cref="Interpolated{T}"/> component instances.
    /// </summary>
    public interface IInterpolated { }

    /// <summary>
    /// Component that holds the interpolated (blended) value of <typeparamref name="T"/>
    /// computed each variable-update frame by <see cref="InterpolatedUpdater{T}"/>.
    /// </summary>
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
