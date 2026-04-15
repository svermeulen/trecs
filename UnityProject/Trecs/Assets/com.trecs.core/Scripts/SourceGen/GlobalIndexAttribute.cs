using System;

namespace Trecs
{
    /// <summary>
    /// Marks an <c>int</c> parameter in an <see cref="IForEachComponentsJob"/> Execute method
    /// to receive the entity's global index across all groups.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When iterating over multiple groups, each entity receives a unique packed index
    /// (0 to total entity count − 1). This is useful for writing entity data into a
    /// contiguous <c>NativeArray</c> shared across all groups.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// [BurstCompile]
    /// public partial struct GatherPositionsJob : IForEachComponentsJob
    /// {
    ///     [WriteOnly] public NativeArray&lt;float3&gt; Output;
    ///
    ///     public readonly void Execute(in CPosition pos, [GlobalIndex] int globalIndex)
    ///     {
    ///         Output[globalIndex] = pos.Value;
    ///     }
    /// }
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
    public class GlobalIndexAttribute : Attribute { }
}
