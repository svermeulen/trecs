using System.Runtime.CompilerServices;
using Trecs.Internal;

// Cannot be Trecs.Internal since it is used in jobs

namespace Trecs
{
    /// <summary>
    /// A read-only, job-friendly view onto the entity indices of a set
    /// for one specific group. The <typeparamref name="TSet"/> generic parameter
    /// tags the view with its target set type at compile time so the
    /// source generator can construct it for a <c>[FromWorld]
    /// NativeEntitySetIndices&lt;TSet&gt;</c> field without an explicit attribute argument.
    /// <para>
    /// Unlike the non-generic <see cref="EntitySetIndices"/> (which is a ref
    /// struct used internally by query iterators), this type is a plain
    /// <c>readonly struct</c> and can be stored as a job field.
    /// </para>
    /// </summary>
    /// <typeparam name="TSet">
    /// The set type these indices belong to. This is a phantom generic — the
    /// runtime layout doesn't reference <typeparamref name="TSet"/> directly, but the
    /// source generator reads it off the field's generic argument to know which set
    /// to fetch the indices for. Do not "simplify" by removing the parameter.
    /// </typeparam>
    public readonly struct NativeEntitySetIndices<TSet>
        where TSet : struct, IEntitySet
    {
        readonly NativeBuffer<int> _indices;
        readonly int _count;

        public NativeEntitySetIndices(NativeBuffer<int> indices, int count)
        {
            _indices = indices;
            _count = count;
        }

        public int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _count;
        }

        public int this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _indices.IndexAsReadOnly(index);
        }
    }
}
