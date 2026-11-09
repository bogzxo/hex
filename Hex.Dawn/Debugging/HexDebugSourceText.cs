using Horizon.UI.Scripting;

namespace Hex.Dawn;

internal static class HexDebugSourceText
{
    internal static string FileName(string name, string? path, string? mimeType, string kind)
    {
        string logical = (name.Length > 0 ? name : path ?? "Source").Replace('\\', '/');
        logical = logical[(logical.LastIndexOf('/') + 1)..].Trim().Trim('<', '>');
        string safe = new(logical.Select(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-' or ' ' ? character : '_').ToArray());
        safe = safe.Trim(' ', '.');
        if (safe.Length == 0) safe = "Source";
        string extension = Path.GetExtension(safe);
        string? native = mimeType?.Split(';')[0].Trim().ToLowerInvariant() switch
        {
            "text/x-python" or "text/python" => ".py",
            "text/x-csharp" or "text/x-cs" => ".cs",
            "text/x-c" => ".c",
            "text/x-c++" or "text/x-c++src" => ".cpp",
            _ => null
        };
        bool codeExtension = extension.ToLowerInvariant() is ".py" or ".pyi" or ".pyw" or ".cs" or ".csx" or ".c" or ".h"
            or ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hxx" or ".d" or ".di" or ".fs" or ".fsx" or ".vb" or ".asm" or ".s";
        if (native is not null || !codeExtension)
        {
            safe = Path.GetFileNameWithoutExtension(safe);
            extension = native ?? (kind == DebugSession.PYTHON ? ".py" : kind == DebugSession.CORECLR ? ".cs" : ".cpp");
        }
        else safe = Path.GetFileNameWithoutExtension(safe);
        if (safe.Length > 100) safe = safe[..100];
        return "Source-" + safe + extension;
    }
}
