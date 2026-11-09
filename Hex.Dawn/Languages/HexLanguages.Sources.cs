using System.Security.Cryptography;
using System.Text;

namespace Hex.Dawn;

internal sealed partial class HexLanguages
{
    internal static string SourceCachePath(string root, string uri)
    {
        string canonical = new Uri(uri).AbsoluteUri;
        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root + "\n" + canonical)));
        string name = Uri.UnescapeDataString(uri[(uri.LastIndexOf('/') + 1)..]);
        char[] invalid = Path.GetInvalidFileNameChars();
        name = new string(name.Select(character => invalid.Contains(character) ? '_' : character).Take(80).ToArray());
        if (name.Length == 0 || name is "." or "..") name = "source.cs";
        if (!name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) name += ".cs";
        return Path.Combine(DecompiledFolder, identity, name);
    }

    private void AttachSource(HexDocument opened, LanguageServer server, string uri)
    {
        if (opened.Path is not {} path) return;
        if (linters.TryGetValue(opened, out var previous) && previous != server) previous.ForgetSource(path);
        server.RegisterSource(path, uri);
        linters[opened] = server;
        opened.Editor.ReadOnly = true;
        ServersChanged++;
    }

    private static bool CanRequest(HexDocument of, string provider) => !of.Editor.ReadOnly || provider is not
        ("renameProvider" or "codeActionProvider" or "documentFormattingProvider" or "documentRangeFormattingProvider" or "documentOnTypeFormattingProvider");
}
