using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;

using Hex.Core;
using Hex.Core.Modules;
using Horizon.Graphics;

namespace Hex.Dawn;

/// <summary>A picture rendered, and how big it is in the layout's units, which is its pixels over the density it was rendered at.</summary>
internal readonly record struct NotebookPixels(ImagePixels Pixels, Vector2 Natural);

/// <summary>
/// CPU rendering of static notebook graphics. Delegates to the optional <c>Hex.Notebooks.Render</c> NativeAOT shared
/// library via C-ABI (with a lazy Debug JIT fallback when running un-published builds) so Hex never loads SkiaSharp,
/// Svg.Skia, or CSharpMath unless the Notebooks module is enabled and a graphic is rendered.
/// </summary>
internal static unsafe partial class NotebookRender
{
    /// <summary>
    /// The fewest and the most pixels a unit of the layout is rendered at. A picture is rendered at the density it's
    /// shown at, a pixel of it to a pixel of the screen.
    /// </summary>
    public const float LeastDensity = 1, MostDensity = 4;

    public const int MaxSource = 4_000_000;
    private const string LibraryName = "Hex.Notebooks.Render";
    private const int ExpectedAbiVersion = 1;

    private static readonly Lock BackendGate = new();
    private static bool backendInitialized;
    private static NativeExports? nativeExports;
    private static ManagedFallback? managedFallback;

    /// <param name="density">How many pixels a unit of the layout is, see <see cref="LeastDensity"/>.</param>
    public static NotebookPixels Svg(string source, float density)
    {
        EnsureEnabled();
        if (source.Length > MaxSource)
            throw new ArgumentException("SVG output exceeds the rendering limit.");

        EnsureBackend();
        if (nativeExports is { } native)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(source);
            fixed (byte* srcPtr = utf8)
            {
                int w = 0, h = 0, pixLen = 0, errLen = 0;
                float natW = 0, natH = 0;
                byte* pixPtr = null, errPtr = null;
                int rc = native.RenderSvg(srcPtr, utf8.Length, density,
                    &w, &h, &natW, &natH, &pixPtr, &pixLen, &errPtr, &errLen);
                return UnpackBitmap(native, rc, w, h, natW, natH, pixPtr, pixLen, errPtr, errLen);
            }
        }

        if (managedFallback is { } managed)
        {
            int rc = managed.RenderSvg(source, density, out int w, out int h, out float natW, out float natH, out byte[] pixels, out string? error);
            return UnpackManagedBitmap(rc, w, h, natW, natH, pixels, error);
        }

        throw new InvalidOperationException("Notebooks rendering module is not installed.");
    }

    /// <param name="density">How many pixels a unit of the layout is, see <see cref="LeastDensity"/>.</param>
    public static NotebookPixels Math(string source, float density, bool prose = false, bool transparent = false, float width = 600, float zoom = 1)
    {
        EnsureEnabled();
        if (source.Length > 32_000)
            throw new ArgumentException("Math output exceeds the rendering limit.");

        EnsureBackend();
        if (nativeExports is { } native)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(source);
            fixed (byte* srcPtr = utf8)
            {
                int w = 0, h = 0, pixLen = 0, errLen = 0;
                float natW = 0, natH = 0;
                byte* pixPtr = null, errPtr = null;
                int rc = native.RenderMath(srcPtr, utf8.Length, density, prose ? 1 : 0, transparent ? 1 : 0, width, zoom,
                    &w, &h, &natW, &natH, &pixPtr, &pixLen, &errPtr, &errLen);
                return UnpackBitmap(native, rc, w, h, natW, natH, pixPtr, pixLen, errPtr, errLen);
            }
        }

        if (managedFallback is { } managed)
        {
            int rc = managed.RenderMath(source, density, prose, transparent, width, zoom, out int w, out int h, out float natW, out float natH, out byte[] pixels, out string? error);
            return UnpackManagedBitmap(rc, w, h, natW, natH, pixels, error);
        }

        throw new InvalidOperationException("Notebooks rendering module is not installed.");
    }

    internal static string Undelimit(string source)
    {
        string text = source.Trim();
        foreach (var (open, close) in new[] { ("$$", "$$"), ("\\[", "\\]"), ("\\(", "\\)"), ("$", "$") })
        {
            if (text.Length >= open.Length + close.Length && text.StartsWith(open, StringComparison.Ordinal) && text.EndsWith(close, StringComparison.Ordinal))
                return text[open.Length..^close.Length].Trim();
        }
        return text;
    }

    private static void EnsureEnabled()
    {
        if (!HexModules.IsEnabled(HexModuleId.Notebooks))
            throw new InvalidOperationException("Notebooks module is disabled.");
    }

    private static NotebookPixels UnpackBitmap(
        NativeExports native, int rc,
        int w, int h, float natW, float natH,
        byte* pixPtr, int pixLen, byte* errPtr, int errLen)
    {
        try
        {
            if (rc == 0)
            {
                byte[] pixels = new byte[pixLen];
                if (pixLen > 0 && pixPtr != null)
                    new ReadOnlySpan<byte>(pixPtr, pixLen).CopyTo(pixels);
                return new NotebookPixels(new ImagePixels(w, h, pixels), new Vector2(natW, natH));
            }

            string message = errPtr != null && errLen > 0
                ? Encoding.UTF8.GetString(new ReadOnlySpan<byte>(errPtr, errLen))
                : "Notebook graphic rendering failed.";
            throw rc == 1 ? new ArgumentException(message) : new InvalidOperationException(message);
        }
        finally
        {
            if (pixPtr != null) native.Free(pixPtr);
            if (errPtr != null) native.Free(errPtr);
        }
    }

    private static NotebookPixels UnpackManagedBitmap(int rc, int w, int h, float natW, float natH, byte[] pixels, string? error)
    {
        if (rc == 0)
            return new NotebookPixels(new ImagePixels(w, h, pixels), new Vector2(natW, natH));
        string message = string.IsNullOrEmpty(error) ? "Notebook graphic rendering failed." : error;
        throw rc == 1 ? new ArgumentException(message) : new InvalidOperationException(message);
    }

    private static void EnsureBackend()
    {
        if (Volatile.Read(ref backendInitialized))
            return;

        lock (BackendGate)
        {
            if (backendInitialized)
                return;

            nativeExports = TryLoadNative();
            if (nativeExports is null && RuntimeFeature.IsDynamicCodeSupported)
                managedFallback = TryLoadManagedFallback();

            Volatile.Write(ref backendInitialized, true);
        }
    }

    private static NativeExports? TryLoadNative()
    {
        HexNativeModule module = HexNativeModule.TryLoad(LibraryName);
        if (!module.IsLoaded)
            return null;

        if (!module.TryGetExport("hex_nb_abi_version", out nint verAddr)
            || !module.TryGetExport("hex_nb_free", out nint freeAddr)
            || !module.TryGetExport("hex_nb_render_svg", out nint svgAddr)
            || !module.TryGetExport("hex_nb_render_math", out nint mathAddr)
            || !module.TryGetExport("hex_nb_render_markdown_math", out nint mdMathAddr)
            || !module.TryGetExport("hex_nb_render_math_svg", out nint mathSvgAddr))
            return null;

        var abiVersion = (delegate* unmanaged[Cdecl]<int>)verAddr;
        if (abiVersion() != ExpectedAbiVersion)
            return null;

        return new NativeExports(freeAddr, svgAddr, mathAddr, mdMathAddr, mathSvgAddr);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Debug JIT fallback only; guarded by RuntimeFeature.IsDynamicCodeSupported and unreachable in NativeAOT.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Debug JIT fallback only; guarded by RuntimeFeature.IsDynamicCodeSupported and unreachable in NativeAOT.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Debug JIT fallback only; guarded by RuntimeFeature.IsDynamicCodeSupported and unreachable in NativeAOT.")]
    private static ManagedFallback? TryLoadManagedFallback()
    {
        string? dllPath = FindManagedModulePath();
        if (dllPath is null)
            return null;

        try
        {
            var alc = new NotebookManagedLoadContext(dllPath);
            Assembly assembly = alc.LoadFromAssemblyPath(dllPath);
            Type? type = assembly.GetType("Hex.Notebooks.Render.NotebookRenderNative");
            if (type is null)
                return null;

            MethodInfo? svgMethod = type.GetMethod("RenderSvgManaged", BindingFlags.Public | BindingFlags.Static);
            MethodInfo? mathMethod = type.GetMethod("RenderMathManaged", BindingFlags.Public | BindingFlags.Static);
            MethodInfo? mdMathMethod = type.GetMethod("RenderMarkdownMathManaged", BindingFlags.Public | BindingFlags.Static);
            MethodInfo? mathSvgMethod = type.GetMethod("RenderMathSvgManaged", BindingFlags.Public | BindingFlags.Static);
            if (svgMethod is null || mathMethod is null || mdMathMethod is null || mathSvgMethod is null)
                return null;

            return new ManagedFallback(
                svgMethod.CreateDelegate<RenderSvgDelegate>(),
                mathMethod.CreateDelegate<RenderMathDelegate>(),
                mdMathMethod.CreateDelegate<RenderMarkdownMathDelegate>(),
                mathSvgMethod.CreateDelegate<RenderMathSvgDelegate>());
        }
        catch
        {
            return null;
        }
    }

    private static string? FindManagedModulePath()
    {
        string[] roots = [AppContext.BaseDirectory, HexHost.ProgramFolder];
        foreach (string root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            string nested = Path.Combine(root, "modules", LibraryName, LibraryName + ".dll");
            if (File.Exists(nested))
                return nested;
            string direct = Path.Combine(root, "modules", LibraryName + ".dll");
            if (File.Exists(direct))
                return direct;
        }
        return null;
    }

    private sealed class NativeExports(nint freeAddr, nint svgAddr, nint mathAddr, nint mdMathAddr, nint mathSvgAddr)
    {
        public readonly delegate* unmanaged[Cdecl]<byte*, void> Free = (delegate* unmanaged[Cdecl]<byte*, void>)freeAddr;

        public readonly delegate* unmanaged[Cdecl]<byte*, int, float, int*, int*, float*, float*, byte**, int*, byte**, int*, int> RenderSvg =
            (delegate* unmanaged[Cdecl]<byte*, int, float, int*, int*, float*, float*, byte**, int*, byte**, int*, int>)svgAddr;

        public readonly delegate* unmanaged[Cdecl]<byte*, int, float, int, int, float, float, int*, int*, float*, float*, byte**, int*, byte**, int*, int> RenderMath =
            (delegate* unmanaged[Cdecl]<byte*, int, float, int, int, float, float, int*, int*, float*, float*, byte**, int*, byte**, int*, int>)mathAddr;

        public readonly delegate* unmanaged[Cdecl]<byte*, int, byte*, int, float, float, float, int*, int*, float*, float*, byte**, int*, byte**, int*, int> RenderMarkdownMath =
            (delegate* unmanaged[Cdecl]<byte*, int, byte*, int, float, float, float, int*, int*, float*, float*, byte**, int*, byte**, int*, int>)mdMathAddr;

        public readonly delegate* unmanaged[Cdecl]<byte*, int, int, byte**, int*, float*, float*, float*, int> RenderMathSvg =
            (delegate* unmanaged[Cdecl]<byte*, int, int, byte**, int*, float*, float*, float*, int>)mathSvgAddr;
    }

    private delegate int RenderSvgDelegate(string source, float density, out int width, out int height, out float natW, out float natH, out byte[] pixels, out string? error);
    private delegate int RenderMathDelegate(string source, float density, bool prose, bool transparent, float width, float zoom, out int outW, out int outH, out float natW, out float natH, out byte[] pixels, out string? error);
    private delegate int RenderMarkdownMathDelegate(string source, string? fontsDir, float density, float width, float zoom, out int outW, out int outH, out float natW, out float natH, out byte[] pixels, out string? error);
    private delegate bool RenderMathSvgDelegate(string tex, bool display, out string? svg, out float width, out float height, out float descent);

    private sealed record ManagedFallback(
        RenderSvgDelegate RenderSvg,
        RenderMathDelegate RenderMath,
        RenderMarkdownMathDelegate RenderMarkdownMath,
        RenderMathSvgDelegate RenderMathSvg);

    private sealed class NotebookManagedLoadContext(string pluginPath) : AssemblyLoadContext("Hex.Notebooks.Render", isCollectible: false)
    {
        private readonly AssemblyDependencyResolver resolver = new(pluginPath);

        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Debug JIT fallback only; unreachable in NativeAOT.")]
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            string? path = resolver.ResolveAssemblyToPath(assemblyName);
            return path is not null ? LoadFromAssemblyPath(path) : null;
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            string? path = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is not null ? LoadUnmanagedDllFromPath(path) : nint.Zero;
        }
    }
}
