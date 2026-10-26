namespace Hex.Dawn;

// The files of the TODO self test, one of each language Hex colours, with a line comment, a block comment over lines
// where the language has one, and a TODO in a string or a name that mustn't be lit. What should be lit is between «
// and », which are taken out before the file is written.
internal sealed partial class DawnScene
{
    private static readonly (string Name, string Sample)[] TodoSamples =
    [
        ("todos.hor", "// «TODO» a line\nlet TODO = \"TODO\";\n/* the first line\n   «FIXME» the second */\n"),
        ("todos.js", "// «TODO» a line\nconst TODO = \"TODO\";\n/*\n * «TODO» the second\n * «FIXME» the third\n */\n"),
        ("todos.ts", "let TODO: string = 'TODO'; // «FIXME» after\n/* the first\n   «TODO» the second */\n"),
        ("todos.py", "# «TODO» a line\nTODO = \"TODO\"  # «FIXME» after\ns = '''\nTODO in a string\n'''\n"),
        ("todos.sh", "# «TODO» a line\necho \"TODO\" # «FIXME» after\n"),
        ("todos.ps1", "# «TODO» a line\n$TODO = \"TODO\"\n<#\n  «FIXME» the second\n#>\n"),
        ("todos.sql", "-- «TODO» a line\nSELECT 'TODO' AS TODO;\n/* the first\n   «FIXME» the second */\n"),
        ("todos.yaml", "# «TODO» a line\nTODO: \"TODO\" # «FIXME» after\n"),
        ("todos.toml", "# «TODO» a line\nTODO = \"TODO\" # «FIXME» after\ns = \"\"\"\nTODO in a string\n\"\"\"\n"),
        ("todos.html", "<!-- «TODO» a line -->\n<p title=\"TODO\">TODO</p>\n<!--\n  «FIXME» the second\n-->\n"),
        ("todos.xml", "<root a=\"TODO\">\n  <!-- «TODO» a line -->\n  <!--\n    «FIXME» the second -->\n  <TODO>TODO</TODO>\n</root>\n"),
        ("todos.css", "/* «TODO» a line */\n.TODO { content: \"TODO\"; }\n/*\n  «FIXME» the second\n*/\n"),
        ("todos.md", "# A heading with TODO\nSome TODO text.\n<!--\n«TODO» the second\n-->\n"),
        ("todos.go", "// «TODO» a line\nvar TODO = \"TODO\"\n/* the first\n   «FIXME» the second */\n"),
        ("todos.rs", "// «TODO» a line\nlet todo = \"TODO\";\n/* the first\n   «FIXME» the second */\n"),
        ("todos.c", "// «TODO» a line\nchar *TODO = \"TODO\";\n/* the first\n * «FIXME» the second\n */\n"),
        ("todos.cpp", "// «TODO» a line\nauto TODO = \"TODO\";\n/* the first\n   «FIXME» the second */\n"),
        ("todos.java", "// «TODO» a line\nString TODO = \"TODO\";\n/**\n * «FIXME» the second\n */\n"),
        ("todos.d", "// «TODO» a line\nstring TODO = \"TODO\";\n/+ the first\n   «FIXME» the second +/\n"),
        ("todos.txt", "«TODO» in plain text everything is a comment, «FIXME» too\nTODOS NOTODO aren't words of it\n")
    ];

    // Helper method to take the marks out of a sample, the text left and every marked word, by line and column
    private static (string Text, List<(int Line, int From, int To, bool Urgent)> Wanted) UnmarkTodos(string sample)
    {
        var wanted = new List<(int, int, int, bool)>();
        string[] lines = sample.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            for (int open = lines[i].IndexOf('«'); open >= 0; open = lines[i].IndexOf('«'))
            {
                int close = lines[i].IndexOf('»', open);
                string word = lines[i][(open + 1)..close];
                wanted.Add((i, open, open + word.Length, word == "FIXME"));
                lines[i] = lines[i][..open] + word + lines[i][(close + 1)..];
            }
        }

        return (string.Join('\n', lines), wanted);
    }
}
