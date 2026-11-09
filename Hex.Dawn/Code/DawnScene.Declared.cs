using Hex.Core;

namespace Hex.Dawn;

// F12 in a language Hex has no server for, a shader's function or a struct, through the agent harness's index of every
// declaration in the workspace. It's what Go to All searches, so F12 finds what Ctrl+T would. The index is woken in the
// background the first time and F12 goes Hex's old way until it has something.
internal sealed partial class DawnScene
{
    // How many places one name can be declared in before the rest aren't listed
    private const int DECLARED_MOST = 50;

    /// <summary>Hands the code editing the index's declarations of a name, exactly that name, with each line's text.</summary>
    private void WireDeclarations()
    {
        codeEditing.Declarations = name =>
        {
            harnessRoots = [.. WorkspaceRoots()];
            HexCodeIndex index = Harness;
            HexIndexSnapshot now = index.Current();
            Task.Run(() => index.Look());

            // Only files of the same kind as the one F12 was pressed in, a shader's beam isn't a Python script's
            string kind = document.Path is { } here ? Path.GetExtension(here) : string.Empty;
            var places = new List<(string Path, int Line, int Column, string Says)>();
            foreach (var (file, symbol, _) in HexCodeIndex.GoToAll(now, name, DECLARED_MOST))
            {
                if (!string.Equals(symbol.Name, name, StringComparison.Ordinal) || !SameKind(kind, Path.GetExtension(file.Path)))
                    continue;

                string[] lines = HexSearch.ReadLines(file.Path);
                places.Add((file.Path, symbol.Line, symbol.Column, symbol.Line < lines.Length ? lines[symbol.Line].Trim() : name));
            }

            return places;
        };
    }

    // Whether two files' endings are one kind of code, the shader languages counting as one (a .slang includes .hlsli)
    private static bool SameKind(string a, string b)
    {
        static string Family(string ending) => ending.ToLowerInvariant() switch
        {
            ".slang" or ".hlsl" or ".hlsli" or ".fx" or ".fxh" => "shader",
            ".c" or ".h" or ".cpp" or ".hpp" or ".cc" or ".hh" or ".cxx" or ".hxx" => "c",
            var other => other
        };

        return Family(a) == Family(b);
    }
}
