namespace Hex.Dawn;

internal sealed partial class HexLanguages
{
    internal static bool MissingNpm(string command, Func<string, string?>? locate = null)
    {
        string executable = command.TrimStart().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        executable = executable.Trim('"', '\'');
        return (executable.Equals("npm", StringComparison.OrdinalIgnoreCase) || executable.Equals("npm.cmd", StringComparison.OrdinalIgnoreCase))
            && (locate ?? HexServerCatalog.Locate)("npm") is null;
    }

    private string NpmHint => context.Strings.Said("language_updates.npm_required");
    private void OpenNpmSetup() => context.Windows.OpenInBrowser("https://docs.npmjs.com/downloading-and-installing-node-js-and-npm/");
}
