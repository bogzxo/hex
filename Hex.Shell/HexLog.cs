using Horizon.Logging;
using Horizon.Logging.Loggers;

namespace Hex.Shell;

/// <summary>One debug.log beside Hex, truncated by every GUI launch; MCP bridges never initialize this logger.</summary>
internal static class HexLog
{
    internal static void Initialize(string folder)
    {
        foreach (string path in Directory.EnumerateFiles(folder, "debug-*.log"))
        {
            string suffix = Path.GetFileNameWithoutExtension(path)[6..];
            if (suffix.Length == 0 || !suffix.All(char.IsAsciiDigit)) continue;
            try
            {
                if (!System.IO.File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) System.IO.File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* An older running process may still own its log. */ }
        }
        Logger.InitializeLogger(new ConcurrentLogger(Path.Combine(folder, "debug.log"), singleFile: true));
    }
}
