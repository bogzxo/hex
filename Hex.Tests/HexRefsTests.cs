using Hex.Core;

namespace Hex.Tests;

/// <summary>
/// hex_refs over the harness's index: a use above a declaration in the same file is listed (the declarations go first and
/// a use further up was taken for written), and Container.Name keeps to the files that say the container.
/// </summary>
public sealed class HexRefsTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("hex-refs").FullName;

    public void Dispose()
    {
        try { Directory.Delete(folder, true); } catch (Exception) { }
    }

    private HexCodeIndex Index() => new(() => [folder]) { StaleAfter = 0 };

    [Fact]
    public void A_use_above_the_declaration_in_its_file_is_listed()
    {
        File.WriteAllText(Path.Combine(folder, "List.cs"), """
            namespace Demo;

            public sealed class List
            {
                public void Text()
                {
                    if (Glyph(1))
                        return;
                }

                private bool Glyph(int at) => at > 0;
            }
            """);

        string said = Index().References("Glyph", null, 80, 8000).Text;

        Assert.Contains("2 places", said);
        Assert.Contains("  7: ", said);
        Assert.Contains("  11*: ", said);
    }

    [Fact]
    public void A_record_struct_declares_its_name_not_struct()
    {
        File.WriteAllText(Path.Combine(folder, "Answer.cs"), "namespace Demo;\n\ninternal readonly record struct Answer(string Text);\npublic record class Other(int A);\n");

        var names = Index().Current().Files.SelectMany(file => file.Symbols).Select(symbol => symbol.Name).ToList();

        Assert.Contains("Answer", names);
        Assert.Contains("Other", names);
        Assert.DoesNotContain("struct", names);
    }

    [Fact]
    public void Container_dot_name_keeps_to_the_files_that_say_the_container()
    {
        File.WriteAllText(Path.Combine(folder, "Draw.cs"), "namespace Demo;\n\npublic sealed class DrawList\n{\n    public void Text() { }\n}\n");
        File.WriteAllText(Path.Combine(folder, "Uses.cs"), "namespace Demo;\n\nstatic class Uses\n{\n    static void Go(DrawList list) => list.Text();\n}\n");
        File.WriteAllText(Path.Combine(folder, "Other.cs"), "namespace Demo;\n\nstatic class Label\n{\n    public static string Text() => \"\";\n}\n");

        string said = Index().References("DrawList.Text", null, 80, 8000).Text;

        Assert.Contains("Only files that say DrawList", said);
        Assert.Contains("Draw.cs", said);
        Assert.Contains("Uses.cs", said);
        Assert.DoesNotContain("Other.cs", said);
    }
}
