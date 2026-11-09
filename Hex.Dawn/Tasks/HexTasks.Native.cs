namespace Hex.Dawn;

internal sealed partial class HexTasks
{
    internal static (string Build, string Run) NativeFileCommands(bool cpp, string file, string exe, string shell, Func<string, string?>? locate = null)
    {
        locate ??= LanguageServerProgram.Locate;
        bool wsl = shell.StartsWith("wsl", StringComparison.OrdinalIgnoreCase);
        bool posix = wsl || shell.Contains("bash", StringComparison.OrdinalIgnoreCase) || !OperatingSystem.IsWindows();
        bool powershell = shell is "powershell" or "pwsh";
        string Quote(string value) => powershell || posix
            ? "'" + value.Replace("'", powershell ? "''" : "'\"'\"'") + "'"
            : "\"" + value + "\"";
        string gcc = cpp ? "g++" : "gcc", clang = cpp ? "clang++" : "clang";
        // WSL's compiler belongs to its distribution; host executables cannot be selected for it.
        string compiler = wsl ? gcc : locate(gcc) ?? locate(clang) ?? gcc;
        if (posix)
        {
            compiler = compiler.Replace('\\', '/');
            file = file.Replace('\\', '/');
        }
        string invoke = powershell ? "& " : string.Empty;
        string run = (posix ? "./" : ".\\") + exe;
        return ($"{invoke}{Quote(compiler)} -g {Quote(file)} -o {Quote(exe)}", invoke + Quote(run));
    }
}
