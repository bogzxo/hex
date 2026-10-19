namespace Hex.Dawn;

/// <summary>
/// The C# side of a built-in extension, for what HIDL can't do quickly enough or at all (the designer, a canvas of a
/// layout's components). The extension's own extension.hor says <c>native: "designer"</c> and is everything else about it,
/// its name, its settings, whether it's switched on, and this is handed the files it says are its own. Only the
/// extensions that came with Dawn get one, an extension somebody put next to it is HIDL and that's all.
/// </summary>
internal interface IHexFileKind
{
    /// <summary>What the extension's file names it by, its native.</summary>
    string Id { get; }

    /// <summary>Whether a file is one of its own, by its path and what's in it.</summary>
    bool Opens(string path, string source);

    /// <summary>Opens one of its own files and shows it, false when it didn't open.</summary>
    bool Open(string path, string source);
}
