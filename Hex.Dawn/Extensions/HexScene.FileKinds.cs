namespace Hex.Dawn;

// The C# sides of the built-in extensions, by the native their extension.hor names. A file goes to the first of them
// that's switched on, started and says it's one of its own, and to the code editor when none does.
internal sealed partial class HexScene
{
    private Dictionary<string, IHexFileKind>? fileKinds;

    private Dictionary<string, IHexFileKind> FileKinds => fileKinds ??= new IHexFileKind[] { new HexDesignerKind(this) }.ToDictionary(kind => kind.Id);

    /// <summary>The built-in extension that opens a file, null for none (it's text).</summary>
    private IHexFileKind? FileKindFor(string path, string source)
    {
        foreach (HexExtension extension in extensions)
        {
            if (extension is { Enabled: true, Failed: null, Native: { } native } && FileKinds.TryGetValue(native, out IHexFileKind? kind) && kind.Opens(path, source))
                return kind;
        }

        return null;
    }

    /// <summary>Whether the designer is switched on, which is whether a layout opens on its canvas.</summary>
    private bool DesignerOn => extensions.Any(extension => extension is { Enabled: true, Failed: null, Native: HexDesignerKind.ID });
}
