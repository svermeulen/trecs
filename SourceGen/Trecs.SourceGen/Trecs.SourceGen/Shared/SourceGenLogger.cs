// Defines:
//   SOURCEGEN_LOGGING  — enables Log() and WriteGeneratedFile()
//   SOURCEGEN_TIMING   — enables Log() only (for SourceGenTimer output)
//   SOURCEGEN_DUMP     — enables WriteGeneratedFile() to write .g.cs files to disk
//
// Add to DefineConstants in the .csproj:
//   <DefineConstants>$(DefineConstants);SOURCEGEN_TIMING</DefineConstants>

#if SOURCEGEN_LOGGING || SOURCEGEN_TIMING || SOURCEGEN_DUMP
#pragma warning disable RS1035 // File I/O is intentional for debug logging
using System.IO;
#endif

namespace Trecs.SourceGen.Shared
{
    internal static class SourceGenLogger
    {
#if SOURCEGEN_LOGGING || SOURCEGEN_TIMING || SOURCEGEN_DUMP
        // We can't use Directory in source gen so we need to use hard coded path
        static readonly string TempDebugDir =
            "/Users/svermeulen/projects/active/temp_source_gen_debugging/SourceGen";

        static readonly string LogFilePath = System.IO.Path.Combine(
            TempDebugDir,
            "SourceGenLog.txt"
        );

        static bool _hasBeenCleared = false;

        public static void ClearLog()
        {
            if (!_hasBeenCleared)
            {
                _hasBeenCleared = true;

                try
                {
                    using StreamWriter writer = new(LogFilePath, false);
                    writer.WriteLine($"[SourceGen] Log cleared at {System.DateTime.Now}");
                    writer.WriteLine($"[SourceGen] Log directory: {TempDebugDir}");
                }
                catch (IOException) { }
                catch (System.UnauthorizedAccessException) { }
            }
        }

        public static void Log(string message)
        {
            ClearLog();
            try
            {
                WriteToFile($"[{System.DateTime.Now:HH:mm:ss.fff}] {message}");
            }
            catch (IOException) { }
            catch (System.UnauthorizedAccessException) { }
        }

        static void WriteToFile(string message)
        {
            using StreamWriter writer = new(LogFilePath, true);
            writer.WriteLine(message);
        }

        public static void WriteGeneratedFile(string fileName, string content)
        {
#if SOURCEGEN_LOGGING || SOURCEGEN_DUMP
            try
            {
                var filePath = System.IO.Path.Combine(TempDebugDir, fileName);

                using StreamWriter writer = new(filePath, false);
                writer.Write(content);

                Log($"Generated file written to: {filePath}");
            }
            catch (IOException ex)
            {
                Log($"Failed to write generated file {fileName}: {ex.Message}");
            }
            catch (System.UnauthorizedAccessException ex)
            {
                Log($"Failed to write generated file {fileName}: {ex.Message}");
            }
#endif
        }
#else
        [System.Diagnostics.Conditional("SOURCEGEN_LOGGING")]
        public static void ClearLog() { }

        [System.Diagnostics.Conditional("SOURCEGEN_LOGGING")]
        public static void Log(string message) { }

        [System.Diagnostics.Conditional("SOURCEGEN_LOGGING")]
        public static void WriteGeneratedFile(string fileName, string content) { }
#endif
    }
}
