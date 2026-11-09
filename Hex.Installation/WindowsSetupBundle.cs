using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Hex.Installation;

/// <summary>The Windows native executable carries its own unpublished executable, libraries and assets in an overlay.</summary>
public static class WindowsSetupBundle
{
    // Must match WindowsSetupBundle.targets. Hash, ZIP length, magic follow the ZIP at the end of the executable.
    private static ReadOnlySpan<byte> Magic => "HexSetupBundle01"u8;
    private const int FooterSize = 32 + sizeof(long) + 16;
    private const long MaxPayloadSize = 1024L * 1024 * 1024;

    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (!OperatingSystem.IsWindows()) return false;
        string executable = Environment.ProcessPath!;
        using var source = File.OpenRead(executable);
        if (!ReadFooter(source, out long length, out byte[] digest)) return false;

        // The installed bootstrap must exit too: otherwise its own image keeps the installation locked.
        string target = InstallPaths.DefaultRoot;
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--target") target = Path.GetFullPath(args[++i]);
        string installed = Path.GetFullPath(Path.Combine(target, "installer"));
        if (Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(executable)!).Equals(installed, StringComparison.OrdinalIgnoreCase))
        {
            InstallerHandoff.StartInstallerCopy(InstallerHandoff.CopyInstaller(installed), args);
            return true;
        }

        string folder = CreateFolder(executable);
        try
        {
            Extract(source, length, digest, folder);
            var start = new ProcessStartInfo(Path.Combine(folder, InstallPaths.InstallerExecutable))
            {
                UseShellExecute = false, WorkingDirectory = folder
            };
            foreach (string arg in args) start.ArgumentList.Add(arg);
            using var child = Process.Start(start) ?? throw new InvalidOperationException();
            child.WaitForExit();
            exitCode = child.ExitCode;
            return true;
        }
        finally { DeleteFolder(folder); }
    }

    internal static string CreateFolder(string executable)
    {
        string folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executable))!, "Hex.Setup-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(folder) || File.Exists(folder)) throw new IOException();
        Directory.CreateDirectory(folder);
        InstallPaths.NoLinks(folder);
        return folder;
    }

    internal static bool ReadFooter(Stream source, out long length, out byte[] digest)
    {
        length = 0;
        digest = [];
        if (source.Length < FooterSize) return false;
        source.Position = source.Length - FooterSize;
        Span<byte> footer = stackalloc byte[FooterSize];
        source.ReadExactly(footer);
        if (!footer[^16..].SequenceEqual(Magic)) return false;
        length = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(footer[32..40]);
        if (length <= 0 || length > MaxPayloadSize || length > source.Length - FooterSize) throw new InvalidDataException();
        digest = footer[..32].ToArray();
        source.Position = source.Length - FooterSize - length;
        return true;
    }

    internal static void Extract(Stream source, long length, byte[] digest, string folder)
    {
        // A disk-backed ZIP keeps memory use small, and bounds the archive independently of the PE overlay.
        string zip = Path.Combine(folder, ".payload-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (var output = new FileStream(zip, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                byte[] buffer = new byte[81920];
                long remaining = length;
                while (remaining > 0)
                {
                    int count = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (count == 0) throw new EndOfStreamException();
                    output.Write(buffer, 0, count);
                    remaining -= count;
                }
                output.Position = 0;
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(output), digest)) throw new InvalidDataException();
            }
            using var archive = ZipFile.OpenRead(zip);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long expanded = 0;
            foreach (var entry in archive.Entries)
            {
                string relative = entry.FullName.TrimEnd('/');
                string path = InstallPaths.Under(folder, relative);
                int kind = (entry.ExternalAttributes >> 16) & 0xf000;
                if (kind is not (0 or 0x8000 or 0x4000) || !names.Add(relative)) throw new InvalidDataException();
                expanded = checked(expanded + entry.Length);
                if (expanded > MaxPayloadSize || names.Count > 100000) throw new InvalidDataException();
                if (entry.Name.Length == 0) { Directory.CreateDirectory(path); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                entry.ExtractToFile(path);
            }
            if (!File.Exists(Path.Combine(folder, InstallPaths.InstallerExecutable))) throw new InvalidDataException();
        }
        finally { if (File.Exists(zip)) File.Delete(zip); }
    }

    internal static void DeleteFolder(string folder)
    {
        // Antivirus and native library teardown can briefly retain handles after the UI process exits.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                InstallPaths.NoLinks(folder);
                Directory.Delete(folder, true);
                return;
            }
            catch (Exception e) when (attempt < 20 && e is IOException or UnauthorizedAccessException) { Thread.Sleep(100); }
        }
    }
}
