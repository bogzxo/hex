namespace Hex.Dawn;

internal sealed partial class HexTasks
{
    // Invoke the environment directly; activation would change the user's terminal session.
    internal static string PythonRun(string from, string root, string file, string shell)
    {
        bool wsl = shell.StartsWith("wsl", StringComparison.OrdinalIgnoreCase);
        bool bash = shell.Contains("bash", StringComparison.OrdinalIgnoreCase) || !OperatingSystem.IsWindows();
        bool powershell = shell is "pwsh" or "powershell";
        string Quote(string value) => powershell || bash
            ? "'" + value.Replace("'", powershell ? "''" : "'\"'\"'") + "'"
            : "\"" + value + "\"";
        string interpreter = wsl || !OperatingSystem.IsWindows() ? "python3" : "python";
        // Windows environments cannot be executed inside WSL.
        if (!wsl && PythonEnvironment.FindLocal(from, root) is { } candidate)
        {
            string relative = Path.GetRelativePath(from, candidate);
            if (bash) relative = relative.Replace('\\', '/');
            if (!Path.IsPathRooted(relative) && !relative.StartsWith('.')) relative = (bash ? "./" : ".\\") + relative;
            return (powershell ? "& " : "") + Quote(relative) + " " + Quote(file);
        }
        return interpreter + " " + Quote(file);
    }
}
