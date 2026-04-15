namespace Trecs
{
    /// <summary>
    /// Declares read-write access to one component type in an IAspect.
    /// The source generator creates <c>ref</c> properties for each declared component, enabling
    /// direct mutation without copying.
    /// </summary>
    /// <remarks>
    /// Up to 8 type parameters are supported per interface. Implement multiple
    /// <see cref="IWrite{T1}"/> interfaces on the same Aspect to declare more than 8 write components.
    /// </remarks>
    /// <example>
    /// <code>
    /// [Aspect]
    /// partial struct DoofusView : IRead&lt;CVelocity&gt;, IWrite&lt;CPosition&gt; { }
    ///
    /// // Generated properties:
    /// //   ref readonly CVelocity Velocity { get; }
    /// //   ref CPosition Position { get; }       (or unwrapped value type if [Unwrap])
    /// //
    /// // Usage: doofus.Position = newPos;  // writes directly to the backing buffer
    /// </code>
    /// </example>
    /// <typeparam name="T1">A component type implementing IEntityComponent.</typeparam>
    /// <seealso cref="IRead{T1}"/>
    public interface IWrite<T1> { }

    /// <summary>Declares read-write access to 2 component types in an Aspect.</summary>
    public interface IWrite<T1, T2> { }

    /// <summary>Declares read-write access to 3 component types in an Aspect.</summary>
    public interface IWrite<T1, T2, T3> { }

    /// <summary>Declares read-write access to 4 component types in an Aspect.</summary>
    public interface IWrite<T1, T2, T3, T4> { }

    /// <summary>Declares read-write access to 5 component types in an Aspect.</summary>
    public interface IWrite<T1, T2, T3, T4, T5> { }

    /// <summary>Declares read-write access to 6 component types in an Aspect.</summary>
    public interface IWrite<T1, T2, T3, T4, T5, T6> { }

    /// <summary>Declares read-write access to 7 component types in an Aspect.</summary>
    public interface IWrite<T1, T2, T3, T4, T5, T6, T7> { }

    /// <summary>Declares read-write access to 8 component types in an Aspect.</summary>
    public interface IWrite<T1, T2, T3, T4, T5, T6, T7, T8> { }
}
