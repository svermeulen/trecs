namespace Trecs
{
    /// <summary>
    /// Declares read-only access to one component type in an IAspect.
    /// The source generator creates <c>ref readonly</c> properties for each declared component,
    /// enabling zero-copy reads.
    /// </summary>
    /// <remarks>
    /// Up to 8 type parameters are supported per interface. Implement multiple
    /// <see cref="IRead{T1}"/> interfaces on the same Aspect to declare more than 8 read components.
    /// </remarks>
    /// <example>
    /// <code>
    /// [Aspect]
    /// partial struct EnemyView : IRead&lt;CPosition, CHealth&gt;, IWrite&lt;CVelocity&gt; { }
    ///
    /// // Generated properties:
    /// //   ref readonly CPosition Position { get; }   (or unwrapped value type if [Unwrap])
    /// //   ref readonly CHealth Health { get; }
    /// </code>
    /// </example>
    /// <typeparam name="T1">A component type implementing IEntityComponent.</typeparam>
    /// <seealso cref="IWrite{T1}"/>
    public interface IRead<T1> { }

    /// <summary>Declares read-only access to 2 component types in an Aspect.</summary>
    public interface IRead<T1, T2> { }

    /// <summary>Declares read-only access to 3 component types in an Aspect.</summary>
    public interface IRead<T1, T2, T3> { }

    /// <summary>Declares read-only access to 4 component types in an Aspect.</summary>
    public interface IRead<T1, T2, T3, T4> { }

    /// <summary>Declares read-only access to 5 component types in an Aspect.</summary>
    public interface IRead<T1, T2, T3, T4, T5> { }

    /// <summary>Declares read-only access to 6 component types in an Aspect.</summary>
    public interface IRead<T1, T2, T3, T4, T5, T6> { }

    /// <summary>Declares read-only access to 7 component types in an Aspect.</summary>
    public interface IRead<T1, T2, T3, T4, T5, T6, T7> { }

    /// <summary>Declares read-only access to 8 component types in an Aspect.</summary>
    public interface IRead<T1, T2, T3, T4, T5, T6, T7, T8> { }
}
