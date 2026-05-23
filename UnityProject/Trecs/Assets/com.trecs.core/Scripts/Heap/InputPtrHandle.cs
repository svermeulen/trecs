using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Trecs
{
    /// <summary>
    /// Handle to an unmanaged allocation owned by <see cref="InputNativeUniqueHeap"/>
    /// (see <see cref="InputNativeUniquePtr{T}"/>). The handle is an opaque
    /// monotonic ID assigned at allocation time. A zero value represents a null
    /// handle.
    ///
    /// <para>Stored as the backing field of <see cref="InputNativeUniquePtr{T}"/>;
    /// resolved through <see cref="Trecs.Internal.InputNativeUniqueResolver"/> on
    /// the Burst side or through the heap directly on the main thread.</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct InputPtrHandle : IEquatable<InputPtrHandle>
    {
        public readonly uint Value;

        public static readonly InputPtrHandle Null;

        internal InputPtrHandle(uint value)
        {
            Value = value;
        }

        public bool IsNull
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return Value == 0; }
        }

        public bool Equals(InputPtrHandle other) => Value == other.Value;

        public override bool Equals(object obj) => obj is InputPtrHandle h && Equals(h);

        public override int GetHashCode() => unchecked((int)Value);

        public static bool operator ==(InputPtrHandle l, InputPtrHandle r) => l.Equals(r);

        public static bool operator !=(InputPtrHandle l, InputPtrHandle r) => !l.Equals(r);

        public override string ToString() => IsNull ? "InputPtr(null)" : $"InputPtr({Value})";
    }
}
