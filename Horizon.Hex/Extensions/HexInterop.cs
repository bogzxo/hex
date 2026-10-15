using System.Diagnostics.CodeAnalysis;

using Horizon.HIDL;
using Horizon.HIDL.Interop;

namespace Horizon.Hex;

/// <summary>
/// The bridge to .NET for extensions, <c>dotnet</c> in their runtime (see HIDLInterop). A published Hex is compiled ahead
/// of time and can't load an assembly while it runs, the bridge says so when it's asked to and an extension that only
/// reaches into what is loaded already (.NET's own types, Horizon's) still can.
/// </summary>
internal static class HexInterop
{
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "An extension asks for types by name on purpose, a trimmed build is told so when it can't.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "An extension asks for types by name on purpose, a native build says it can't load assemblies.")]
    public static void Declare(HIDLRuntime runtime, string folder) => HIDLInterop.Declare(runtime, "dotnet", folder);
}
