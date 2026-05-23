using System;

namespace Trecs
{
    /// <summary>
    /// Apply to a component struct to exclude its per-entity values from
    /// per-frame checksum streams (payloads serialized with
    /// <see cref="SerializationFlags.IsForChecksum"/>). The component's array
    /// length still participates in the wire format — only the entry values
    /// are skipped — so the checksum stream stays aligned across all
    /// component types in a group.
    ///
    /// <para>
    /// Use this for components whose values are deterministic across a single
    /// run but noisy in ways that would cause false-positive desyncs when
    /// compared across runs / machines / builds (e.g. high-precision floats
    /// computed differently by different optimizers, debug counters that
    /// change between Debug and Release builds). The component still survives
    /// snapshots and recordings intact — its values are only stripped when
    /// the serializer is in checksum mode.
    /// </para>
    ///
    /// <para>
    /// For state that should not be persisted at all (transient single-frame
    /// buffers, runtime handles), register a custom
    /// <see cref="IComponentArraySerializer{T}"/> like
    /// <c>SkipComponentSerializer&lt;T&gt;</c> or
    /// <c>DefaultValueComponentSerializer&lt;T&gt;</c> instead — those affect
    /// snapshots and recordings, not just checksums.
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
    public sealed class ChecksumIgnoreAttribute : Attribute { }
}
