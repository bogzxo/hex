using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace Hex.Dusk;

/// <summary>
/// Dawn, from Dusk's side. A file opened in the Dawn that is running when there is one (it listens on a pipe of its own,
/// DawnScene.Remote, a line "open|path|line"), started for the file when there isn't. Dusk is a mode of Hex.exe, so Dawn
/// is the same exe started without --dusk, unless <c>DUSK_HEX</c> says another (none for no Dawn at all).
/// </summary>
internal static class HexLauncher
{
    /// <summary>The pipe a running Hex listens on, the user's name in it so two people on one machine don't meet.</summary>
    public static string PipeName => Environment.GetEnvironmentVariable("DUSK_HEX_PIPE") is { Length: > 0 } told ? told : "horizon-hex-" + Environment.UserName.ToLowerInvariant();

    /// <summary>Opens a file in Hex at a line (1 for the first, 0 for wherever), false when there is no Hex to open it in.</summary>
    public static bool Open(string path, int line = 0)
    {
        path = Path.GetFullPath(path);
        if (Tell($"open|{path}|{line}"))
            return true;

        if (Find() is not { } hex)
            return false;

        try
        {
            var start = new ProcessStartInfo(hex) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(hex)! };
            start.ArgumentList.Add("--open");
            start.ArgumentList.Add(path);
            start.ArgumentList.Add("--workspace");
            start.ArgumentList.Add(Path.GetDirectoryName(path)!);
            if (line > 0)
            {
                start.ArgumentList.Add("--line");
                start.ArgumentList.Add(line.ToString());
            }

            Process.Start(start);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>A folder for Dawn, the workspace of the Dawn that's running, or a new one started on it.</summary>
    public static bool OpenFolder(string folder)
    {
        folder = Path.GetFullPath(folder);
        return Tell($"folder|{folder}") || Start(folder, window: false);
    }

    /// <summary>A Dawn window of its own on a file or a folder, whatever Dawn is running, false when there's no Hex.</summary>
    public static bool Window(string path) => Start(Path.GetFullPath(path), window: true);

    private static bool Start(string path, bool window)
    {
        if (Find() is not { } hex)
            return false;

        try
        {
            var start = new ProcessStartInfo(hex) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(hex)! };
            if (window)
                start.ArgumentList.Add("--window");
            start.ArgumentList.Add(Directory.Exists(path) ? "--folder" : "--open");
            start.ArgumentList.Add(path);
            Process.Start(start);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Says a line to the Hex that is running, false when none is listening.</summary>
    public static bool Tell(string message)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(150);
            byte[] bytes = Encoding.UTF8.GetBytes(message + "\n");
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Where Hex's exe is, null when it can't be found.</summary>
    public static string? Find()
    {
        // DUSK_HEX=none is no Dawn at all, what the self test says so it never starts one
        if (Environment.GetEnvironmentVariable("DUSK_HEX") is { Length: > 0 } told)
            return told == "none" ? null : File.Exists(told) ? told : null;

        // the exe Dusk is running in is Dawn as well
        if (Environment.ProcessPath is { } self && Path.GetFileNameWithoutExtension(self).Equals("Hex", StringComparison.OrdinalIgnoreCase))
            return self;

        string beside = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Hex.exe" : "Hex");
        return File.Exists(beside) ? beside : null;
    }
}
