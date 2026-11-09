using Hex.Dawn;

namespace Hex.Tests;

public sealed class NativeFileCommandsTests
{
    [Theory]
    [InlineData(true, "clang++")]
    [InlineData(false, "clang")]
    public void UsesInstalledClangWhenGccIsMissing(bool cpp, string expected)
    {
        var commands = HexTasks.NativeFileCommands(cpp, "hello world.cpp", "hello world.exe", "pwsh", name => name == expected ? "C:/Program Files/LLVM/bin/" + name + ".exe" : null);
        Assert.Equal($"& 'C:/Program Files/LLVM/bin/{expected}.exe' -g 'hello world.cpp' -o 'hello world.exe'", commands.Build);
        Assert.Equal("& '.\\hello world.exe'", commands.Run);
    }

    [Fact]
    public void KeepsGccPreferenceAndEscapesPowerShellLiterals()
    {
        var commands = HexTasks.NativeFileCommands(true, "a'$b.cpp", "a'$b.exe", "pwsh", name => name);
        Assert.Equal("& 'g++' -g 'a''$b.cpp' -o 'a''$b.exe'", commands.Build);
        Assert.Equal("& '.\\a''$b.exe'", commands.Run);
    }

    [Fact]
    public void WslDoesNotProbeHostCompilers()
    {
        var commands = HexTasks.NativeFileCommands(true, "main.cpp", "main", "wsl", _ => throw new Exception("Host lookup"));
        Assert.Equal("'g++' -g 'main.cpp' -o 'main'", commands.Build);
        Assert.Equal("'./main'", commands.Run);
    }
}
