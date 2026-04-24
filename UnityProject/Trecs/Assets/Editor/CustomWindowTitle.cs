using System.IO;
using UnityEditor;
using UnityEngine;

namespace Trecs.Samples
{
    [InitializeOnLoad]
    public static class CustomWindowTitle
    {
        static CustomWindowTitle()
        {
            EditorApplication.updateMainWindowTitle += UpdateTitle;
        }

        static readonly string[] Dots = { "🔴", "🟠", "🟡", "🟢", "🔵", "🟣", "🟤", "⚫", "⚪" };

        static void UpdateTitle(ApplicationTitleDescriptor desc)
        {
            var projectDir = new DirectoryInfo(Path.Combine(Application.dataPath, "../../..")).Name.ToUpper();
            var dot = Dots[(uint)projectDir.GetHashCode() % Dots.Length];
            desc.title = $"{dot} External Trecs {dot} - {projectDir}";
        }
    }
}
