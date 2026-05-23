using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Trecs.Internal
{
    // 256-bit bitfield, one bit per component slot on a template. Replaces the
    // single-ulong encoding used by FastAddSlotHeader.SetMask and
    // NativeTemplateLayoutHeader.ZeroDefaultMask. Blittable, Burst-safe, no
    // managed paths. Four explicit ulong fields rather than a `fixed ulong
    // Words[4]` so the consumers (FastAddSlotHeader, NativeTemplateLayoutHeader)
    // stay non-`unsafe` structs; the hot path that needs index-by-pointer
    // reinterprets via SetUnsafe.
    [EditorBrowsable(EditorBrowsableState.Never)]
    [StructLayout(LayoutKind.Sequential)]
    internal struct TemplateComponentMask
    {
        public ulong Word0;
        public ulong Word1;
        public ulong Word2;
        public ulong Word3;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(int ci)
        {
            switch (ci >> 6)
            {
                case 0:
                    Word0 |= 1ul << (ci & 63);
                    break;
                case 1:
                    Word1 |= 1ul << (ci & 63);
                    break;
                case 2:
                    Word2 |= 1ul << (ci & 63);
                    break;
                default:
                    Word3 |= 1ul << (ci & 63);
                    break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsSet(int ci)
        {
            ulong bit = 1ul << (ci & 63);
            switch (ci >> 6)
            {
                case 0:
                    return (Word0 & bit) != 0;
                case 1:
                    return (Word1 & bit) != 0;
                case 2:
                    return (Word2 & bit) != 0;
                default:
                    return (Word3 & bit) != 0;
            }
        }

        // Pointer-flavor setter for the AddEntity hot path that already holds
        // a TemplateComponentMask*. Reinterprets as ulong* to emit a single
        // indexed word write, matching the original `*ptr |= 1ul << i` codegen.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe void SetUnsafe(TemplateComponentMask* p, int ci)
        {
            ulong* words = (ulong*)p;
            words[ci >> 6] |= 1ul << (ci & 63);
        }
    }
}
