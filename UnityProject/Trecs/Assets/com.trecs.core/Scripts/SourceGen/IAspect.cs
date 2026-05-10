using System.ComponentModel;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Marker interface for Aspect types — bundles of components that systems
    /// declare as a single-shot read / write surface over an entity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An Aspect is a <c>partial struct</c> that implements <c>IAspect</c>
    /// plus any combination of <see cref="IRead{T1}"/> / <see cref="IWrite{T1}"/>
    /// (1‑arity through 8‑arity). The source generator emits:
    /// </para>
    /// <list type="bullet">
    /// <item><description>A field of type <see cref="EntityIndex"/> identifying the entity the aspect is currently iterating / pointing at.</description></item>
    /// <item><description>A property per declared component (<c>ref readonly</c> for reads, <c>ref</c> for writes).</description></item>
    /// <item><description>Constructors taking <see cref="EntityIndex"/>, an entity-handle, or a query iteration handle so the aspect can be created from any of the three primary entry points.</description></item>
    /// <item><description>A nested <c>NativeFactory</c> for creating aspects inside Burst jobs without going through managed query plumbing.</description></item>
    /// </list>
    /// <para>
    /// Tags are not part of the Aspect — they're specified at iteration time
    /// (<c>[ForEachEntity(typeof(MyTag))]</c> on the iterating method, or
    /// <c>MyAspect.Query(Ecs).WithTags&lt;MyTag&gt;()</c> for fluent queries).
    /// This keeps an Aspect a pure component bundle that's reusable across
    /// any entity that has the listed components, regardless of which
    /// templates / tags they came from.
    /// </para>
    /// <para>
    /// Aspects are jobs-friendly — they store no managed references, only
    /// pointers and indices, so passing them through Unity jobs (including
    /// Burst-compiled ones) works without extra ceremony.
    /// </para>
    /// <example>
    /// <code>
    /// partial struct PlayerView : IAspect, IRead&lt;CHealth, CPosition&gt;, IWrite&lt;CBatteryLevel&gt;
    /// {
    /// }
    ///
    /// [ForEachEntity(typeof(EcsTags.LocalPlayer))]
    /// void Tick(ref PlayerView player)
    /// {
    ///     var pos = player.Position;        // ref readonly CPosition
    ///     player.BatteryLevel = 0.5f;       // ref CBatteryLevel
    /// }
    /// </code>
    /// </example>
    /// </remarks>
    public interface IAspect
    {
        /// <summary>
        /// The entity index this aspect points at. Set by the constructor /
        /// iteration framework — user code should not assign to it.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        EntityIndex EntityIndex { get; }
    }
}
