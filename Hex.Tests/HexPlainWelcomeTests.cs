using Hex.Dawn;

namespace Hex.Tests;

/// <summary>
/// Whether a folder Hex is started on has layouts near its top, the designer's welcome, or is code, the plain one. The
/// engine's repository has a tasks.hor at its top and nothing else, and it opened on an empty layout in the designer.
/// </summary>
public sealed class HexPlainWelcomeTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "hex-welcome-" + Guid.NewGuid().ToString("N")[..8]);

    public HexPlainWelcomeTests() => Directory.CreateDirectory(Path.Combine(folder, "Engine"));

    public void Dispose()
    {
        try { Directory.Delete(folder, true); } catch (Exception) { }
    }

    [Fact]
    public void A_tasks_file_at_the_top_is_not_a_layout()
    {
        File.WriteAllText(Path.Combine(folder, "tasks.hor"), "let tasks = {}");
        File.WriteAllText(Path.Combine(folder, "Engine", "Engine.cs"), "class Engine { }");
        Assert.False(DawnScene.HasLayoutsNear([folder]));
    }

    [Fact]
    public void A_layout_near_the_top_still_is()
    {
        File.WriteAllText(Path.Combine(folder, "tasks.hor"), "let tasks = {}");
        File.WriteAllText(Path.Combine(folder, "Engine", "menu.hor"), "let menu = Panel {}");
        Assert.True(DawnScene.HasLayoutsNear([folder]));
    }
}
