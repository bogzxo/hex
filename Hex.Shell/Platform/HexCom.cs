using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Hex.Shell;

/// <summary>Helper method to ask COM for a class from a server of its own, for finding out whether COM would start one.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class HexCom
{
    private const uint CLSCTX_LOCAL_SERVER = 0x4, COINIT_MULTITHREADED = 0;

    /// <summary>The HRESULT of asking COM for a class from a local server, what it made let go of straight away. The calling thread is put in the multithreaded apartment.</summary>
    public static int CreateLocal(Guid clsid)
    {
        CoInitializeEx(0, COINIT_MULTITHREADED);
        Guid unknown = new("00000000-0000-0000-C000-000000000046");
        nint made = 0;
        int said = CoCreateInstance(&clsid, 0, CLSCTX_LOCAL_SERVER, &unknown, &made);
        if (made != 0)
            Marshal.Release(made);
        return said;
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint model);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* made);
}
