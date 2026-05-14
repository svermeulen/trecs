using System.Collections.Generic;
using System.ComponentModel;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class GroupTagNames
    {
        static readonly Dictionary<int, string> _names = new();

        public static void StoreName(int guid, string debugName)
        {
            TrecsAssert.That(UnityThreadHelper.IsMainThread);
            if (_names.TryGetValue(guid, out var existingName))
            {
                if (existingName != debugName)
                {
                    throw new TrecsException(
                        $"Tag GUID collision: '{debugName}' has the same GUID ({guid}) as '{existingName}'. "
                            + "Rename one tag or use the explicit Tag(uint, string) constructor."
                    );
                }
                return;
            }
            _names.Add(guid, debugName);
        }

        public static string GetName(int guid)
        {
            TrecsAssert.That(UnityThreadHelper.IsMainThread);
            return _names[guid];
        }
    }
}
