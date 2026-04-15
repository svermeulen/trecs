using UnityEditor;

namespace Trecs.Samples
{
    [InitializeOnLoad]
    public static class CustomWindowTitle
    {
        static CustomWindowTitle()
        {
            EditorApplication.updateMainWindowTitle += UpdateTitle;
        }

        static void UpdateTitle(ApplicationTitleDescriptor desc)
        {
            desc.title = "🟡 External Trecs 🟡";
        }
    }
}
