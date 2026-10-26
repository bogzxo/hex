using Hex.Core.Projects;

namespace Hex.Tests;

/// <summary>
/// The edits a file's properties make to its project, the text before and after, every other byte of it left alone.
/// Visual Studio writes the same ones, so a project edited in either reads the same in the other.
/// </summary>
public sealed class ProjectFileEditsTests
{
    private const string PLAIN = """
        <Project Sdk="Microsoft.NET.Sdk">

          <!-- what it is -->
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>

        </Project>
        """;

    [Fact]
    public void CopyIfNewerIsAnUpdateItemInAnItemGroupOfItsOwn()
    {
        string after = ProjectFileEdits.SetMetadata(PLAIN, "None", "data\\a.txt", ProjectFileEdits.COPY_TO_OUTPUT, "PreserveNewest");

        Assert.Equal("""
            <Project Sdk="Microsoft.NET.Sdk">

              <!-- what it is -->
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>

              <ItemGroup>
                <None Update="data\a.txt">
                  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
                </None>
              </ItemGroup>

            </Project>
            """, after);
    }

    [Fact]
    public void SetBackToDoNotCopyTheProjectIsWhatItWas()
    {
        string copied = ProjectFileEdits.SetMetadata(PLAIN, "None", "data\\a.txt", ProjectFileEdits.COPY_TO_OUTPUT, "PreserveNewest");
        string back = ProjectFileEdits.SetMetadata(copied, "None", "data\\a.txt", ProjectFileEdits.COPY_TO_OUTPUT, null);

        Assert.Equal(PLAIN, back);
    }

    [Fact]
    public void ASecondFileGoesInTheSameGroupAndAChangeChangesOnlyTheValue()
    {
        string one = ProjectFileEdits.SetMetadata(PLAIN, "None", "a.txt", ProjectFileEdits.COPY_TO_OUTPUT, "PreserveNewest");
        string two = ProjectFileEdits.SetMetadata(one, "None", "b.txt", ProjectFileEdits.COPY_TO_OUTPUT, "Always");
        string changed = ProjectFileEdits.SetMetadata(two, "None", "a.txt", ProjectFileEdits.COPY_TO_OUTPUT, "Always");

        Assert.Equal(1, Count(two, "<ItemGroup>"));
        Assert.Contains("    <None Update=\"a.txt\">\n      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>\n    </None>\n    <None Update=\"b.txt\">", two);
        Assert.Equal(two.Replace("PreserveNewest", "Always"), changed);

        // Both back to the default, and the group goes with the last of them
        string none = ProjectFileEdits.SetMetadata(ProjectFileEdits.SetMetadata(changed, "None", "a.txt", ProjectFileEdits.COPY_TO_OUTPUT, null), "None", "b.txt", ProjectFileEdits.COPY_TO_OUTPUT, null);
        Assert.Equal(PLAIN, none);
    }

    [Fact]
    public void AnAttributeIsChangedWhereItIsAndCRLFIsKept()
    {
        string project = "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n\t<ItemGroup>\r\n\t\t<None Update='x.json' CopyToOutputDirectory='Always' />\r\n\t</ItemGroup>\r\n</Project>\r\n";

        string changed = ProjectFileEdits.SetMetadata(project, "None", "x.json", ProjectFileEdits.COPY_TO_OUTPUT, "PreserveNewest");
        Assert.Equal(project.Replace("'Always'", "'PreserveNewest'"), changed);

        // Taken off, the Update has nothing left and goes, and the group with it
        string off = ProjectFileEdits.SetMetadata(project, "None", "x.json", ProjectFileEdits.COPY_TO_OUTPUT, null);
        Assert.Equal("<Project Sdk=\"Microsoft.NET.Sdk\">\r\n</Project>\r\n", off);
    }

    [Fact]
    public void AnIncludeItemKeepsItsIncludeAndGetsTheMetadataInside()
    {
        string project = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <Content Include=\"web\\site.css\" />\n  </ItemGroup>\n</Project>\n";

        string copied = ProjectFileEdits.SetMetadata(project, "Content", "web/site.css", ProjectFileEdits.COPY_TO_OUTPUT, "Always");
        Assert.Equal("<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <Content Include=\"web\\site.css\">\n      <CopyToOutputDirectory>Always</CopyToOutputDirectory>\n    </Content>\n  </ItemGroup>\n</Project>\n", copied);

        // And back, closed on itself again, not taken away
        Assert.Equal(project, ProjectFileEdits.SetMetadata(copied, "Content", "web\\site.css", ProjectFileEdits.COPY_TO_OUTPUT, null));
    }

    [Fact]
    public void ACommentInAGroupStaysAndAGroupOfAnotherKindIsntUsed()
    {
        string project = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <!-- packages -->\n    <PackageReference Include=\"xunit\" Version=\"2.9.3\" />\n  </ItemGroup>\n</Project>\n";

        string copied = ProjectFileEdits.SetMetadata(project, "None", "a.txt", ProjectFileEdits.COPY_TO_OUTPUT, "Always");
        Assert.StartsWith("<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <!-- packages -->\n    <PackageReference Include=\"xunit\" Version=\"2.9.3\" />\n  </ItemGroup>\n\n  <ItemGroup>\n    <None Update=\"a.txt\">", copied);
        Assert.Equal(project, ProjectFileEdits.SetMetadata(copied, "None", "a.txt", ProjectFileEdits.COPY_TO_OUTPUT, null));
    }

    [Fact]
    public void BuildActionFromAGlobIsARemoveAndAnInclude()
    {
        string content = ProjectFileEdits.SetItemType(PLAIN, "data.txt", "None", "Content");
        Assert.Contains("  <ItemGroup>\n    <None Remove=\"data.txt\" />\n  </ItemGroup>\n\n  <ItemGroup>\n    <Content Include=\"data.txt\" />\n  </ItemGroup>\n", content);

        // And back the way it came, nothing of either left
        Assert.Equal(PLAIN, ProjectFileEdits.SetItemType(content, "data.txt", "Content", "None"));
    }

    [Fact]
    public void BuildActionTakesTheCopySettingWithIt()
    {
        string copied = ProjectFileEdits.SetMetadata(PLAIN, "None", "data.txt", ProjectFileEdits.COPY_TO_OUTPUT, "PreserveNewest");
        string content = ProjectFileEdits.SetItemType(copied, "data.txt", "None", "Content");

        Assert.DoesNotContain("<None Update", content);
        Assert.Contains("<None Remove=\"data.txt\" />", content);
        Assert.Contains("<Content Include=\"data.txt\">\n      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>\n    </Content>", content);
    }

    [Fact]
    public void ACSharpFileMadeNoneIsACompileRemoveAndANoneInclude()
    {
        string none = ProjectFileEdits.SetItemType(PLAIN, "Old\\Thing.cs", "Compile", "None");
        Assert.Contains("<Compile Remove=\"Old\\Thing.cs\" />", none);
        Assert.Contains("<None Include=\"Old\\Thing.cs\" />", none);
        Assert.Equal(PLAIN, ProjectFileEdits.SetItemType(none, "Old\\Thing.cs", "None", "Compile"));
    }

    [Fact]
    public void OneFileOfAListOfThemComesOutOfTheList()
    {
        string project = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <Content Include=\"a.txt;b.txt\" />\n  </ItemGroup>\n</Project>\n";
        string after = ProjectFileEdits.SetItemType(project, "b.txt", "Content", "EmbeddedResource");

        Assert.Contains("<Content Include=\"a.txt\" />", after);
        Assert.Contains("<EmbeddedResource Include=\"b.txt\" />", after);
    }

    [Fact]
    public void WhatTheProjectSaysOfAFileIsFound()
    {
        string copied = ProjectFileEdits.SetMetadata(PLAIN, "None", "a.txt", ProjectFileEdits.COPY_TO_OUTPUT, "Always");
        Assert.True(ProjectFileEdits.SaysMetadata(copied, "a.txt", ProjectFileEdits.COPY_TO_OUTPUT));
        Assert.False(ProjectFileEdits.SaysMetadata(copied, "b.txt", ProjectFileEdits.COPY_TO_OUTPUT));
        Assert.False(ProjectFileEdits.SaysMetadata(PLAIN, "a.txt", ProjectFileEdits.COPY_TO_OUTPUT));
    }

    [Fact]
    public void AProjectThatClosesOnItselfIsOpenedUp()
    {
        string after = ProjectFileEdits.SetMetadata("<Project Sdk=\"Microsoft.NET.Sdk\" />\n", "None", "a.txt", ProjectFileEdits.COPY_TO_OUTPUT, "Always");
        Assert.Equal("<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <None Update=\"a.txt\">\n      <CopyToOutputDirectory>Always</CopyToOutputDirectory>\n    </None>\n  </ItemGroup>\n</Project>\n", after);
    }

    [Fact]
    public void TheXmlReaderFindsWhereThingsAre()
    {
        var root = ProjectXml.Read("<?xml version=\"1.0\"?>\n<Project Sdk='X'><!-- <Fake/> --><ItemGroup><None Include=\"a &amp; b\"/></ItemGroup></Project>");
        Assert.NotNull(root);
        Assert.Equal("Project", root.Name);
        ProjectXmlElement none = root.Children.Single().Children.Single();
        Assert.Equal("None", none.Name);
        Assert.Equal("a & b", none.Attribute("Include")!.Value);
        Assert.Null(ProjectXml.Read("<Project><ItemGroup></Project>"));
    }

    private static int Count(string text, string what) => (text.Length - text.Replace(what, string.Empty).Length) / what.Length;
}
