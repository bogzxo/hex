using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Hex.Core.Modules;

/// <summary>
/// Cross-platform NativeAOT shared library loader for optional Hex modules. Libraries live in
/// <c>&lt;ProgramFolder&gt;/modules/</c> (with a fallback next to the executable) and are never loaded into memory
/// until first requested while their module is enabled.
/// </summary>
public sealed class HexNativeModule
{
    private static readonly ConcurrentDictionary<string, HexNativeModule> Loaded = new(StringComparer.OrdinalIgnoreCase);

    private readonly string baseName;
    private readonly nint handle;
    private readonly string? resolvedPath;
    private readonly string? loadError;

    private HexNativeModule(string baseName, nint handle, string? resolvedPath, string? loadError)
    {
        this.baseName = baseName;
        this.handle = handle;
        this.resolvedPath = resolvedPath;
        this.loadError = loadError;
    }

    /// <summary>Whether the native library was loaded successfully.</summary>
    public bool IsLoaded => handle != 0;

    /// <summary>Absolute path of the loaded library, or null if not loaded.</summary>
    public string? ResolvedPath => resolvedPath;

    /// <summary>Diagnostic reason why loading failed, or null on success.</summary>
    public string? LoadError => loadError;

    /// <summary>Checks whether the platform-specific native module binary exists on disk without loading it.</summary>
    public static bool ExistsOnDisk(string baseName, string? programFolder = null) =>
        FindOnDisk(baseName, programFolder) is not null;

    /// <summary>Returns the first matching path on disk for <paramref name="baseName"/>, or null if absent.</summary>
    public static string? FindOnDisk(string baseName, string? programFolder = null)
    {
        bool explicitFolder = !string.IsNullOrWhiteSpace(programFolder);
        string root = explicitFolder ? programFolder! : HexHost.ProgramFolder;
        bool sameAsBase = string.Equals(root, AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase);

        foreach (string fileName in CandidateFileNames(baseName))
        {
            if (explicitFolder)
            {
                string inModules = Path.Combine(root, "modules", fileName);
                if (File.Exists(inModules))
                    return inModules;

                string besideExe = Path.Combine(root, fileName);
                if (File.Exists(besideExe))
                    return besideExe;

                if (!sameAsBase)
                {
                    string inBaseModules = Path.Combine(AppContext.BaseDirectory, "modules", fileName);
                    if (File.Exists(inBaseModules))
                        return inBaseModules;

                    string inBase = Path.Combine(AppContext.BaseDirectory, fileName);
                    if (File.Exists(inBase))
                        return inBase;
                }
            }
            else
            {
                // In macOS Hex.app, AppContext.BaseDirectory is Contents/MacOS/ where signed .dylibs live,
                // while HexHost.ProgramFolder is Contents/Resources/. Check AppContext.BaseDirectory/modules first.
                string inBaseModules = Path.Combine(AppContext.BaseDirectory, "modules", fileName);
                if (File.Exists(inBaseModules))
                    return inBaseModules;

                if (!sameAsBase)
                {
                    string inProgramModules = Path.Combine(root, "modules", fileName);
                    if (File.Exists(inProgramModules))
                        return inProgramModules;
                }

                string inBase = Path.Combine(AppContext.BaseDirectory, fileName);
                if (File.Exists(inBase))
                    return inBase;

                if (!sameAsBase)
                {
                    string besideProgram = Path.Combine(root, fileName);
                    if (File.Exists(besideProgram))
                        return besideProgram;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Loads the optional native module once and caches the result. Never throws if the file is missing so disabled or
    /// uninstalled modules degrade cleanly.
    /// </summary>
    public static HexNativeModule TryLoad(string baseName, string? programFolder = null)
    {
        return Loaded.GetOrAdd(baseName, name =>
        {
            string? path = FindOnDisk(name, programFolder);
            if (path is null)
                return new HexNativeModule(name, 0, null, $"Optional module library '{name}' is not installed.");

            if (NativeLibrary.TryLoad(path, out nint h))
                return new HexNativeModule(name, h, path, null);

            return new HexNativeModule(name, 0, path, $"Failed to load optional module library at '{path}'.");
        });
    }

    /// <summary>Looks up a C-ABI exported function pointer from the loaded native module.</summary>
    public bool TryGetExport(string symbol, out nint address)
    {
        if (handle == 0)
        {
            address = 0;
            return false;
        }
        return NativeLibrary.TryGetExport(handle, symbol, out address);
    }

    /// <summary>Returns candidate file names for the current OS (Windows .dll, macOS .dylib, Linux .so).</summary>
    public static string[] CandidateFileNames(string baseName)
    {
        if (OperatingSystem.IsWindows())
            return [$"{baseName}.dll"];
        if (OperatingSystem.IsMacOS())
            return [$"lib{baseName}.dylib", $"{baseName}.dylib"];
        return [$"lib{baseName}.so", $"{baseName}.so"];
    }
}
