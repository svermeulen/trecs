namespace Trecs
{
    /// <summary>
    /// Marker interface for Aspect types. Aspects are partial structs that declare
    /// type-safe component access via <see cref="IRead{T1}"/> and <see cref="IWrite{T1}"/>
    /// interfaces. The source generator produces component access properties, constructors,
    /// and iteration helpers.
    /// </summary>
    public interface IAspect { }
}
