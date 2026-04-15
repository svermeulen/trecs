using System.ComponentModel;
using System.Runtime.CompilerServices;
using Trecs.Collections;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class ExclusiveGroupExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool FoundIn(this in Group group, Group[] groups)
        {
            for (int i = 0; i < groups.Length; ++i)
                if (groups[i] == group)
                    return true;

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool FoundIn(this in Group group, FastList<Group> groups)
        {
            for (int i = 0; i < groups.Count; ++i)
                if (groups[i] == group)
                    return true;

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool FoundIn(this in Group group, LocalReadOnlyFastList<Group> groups)
        {
            for (int i = 0; i < groups.Count; ++i)
                if (groups[i] == group)
                    return true;

            return false;
        }
    }
}
