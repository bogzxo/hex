namespace Hex.Dawn;

/// <summary>
/// The designer as the built-in extension it is (Assets/hex/extensions/designer), the .hor files that say #define layout
/// opened on a canvas rather than as their code. Switched off in the extensions and a layout is the HIDL it is, the code
/// only, the way any other file of HIDL opens. The canvas, the palette, the inspector and the rest are still in the
/// scene's Designer folder, this is the door the shell goes through to them.
/// </summary>
internal sealed class HexDesignerKind(DawnScene scene) : IHexFileKind
{
    public const string ID = "designer";

    public string Id => ID;

    public bool Opens(string path, string source) =>
        Path.GetExtension(path).Equals(".hor", StringComparison.OrdinalIgnoreCase) && Horizon.HIDL.Preprocessing.HIDLPreprocessor.IsLayout(source);

    public bool Open(string path, string source) => scene.OpenLayout(path, source);
}
