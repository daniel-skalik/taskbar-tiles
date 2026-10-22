using System.IO;
using TaskbarTiles.Plugins.Script;

namespace TaskbarTiles.Tests;

public class ExecutableLocatorTests
{
    [Fact]
    public void Finds_a_file_on_the_given_path()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("tool.exe"), "");

        Assert.Equal(dir.File("tool.exe"), ExecutableLocator.FindOnPath("tool.exe", @"C:\does-not-exist;" + dir.Path));
    }

    [Fact]
    public void Quoted_and_odd_entries_are_handled()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("tool.exe"), "");

        Assert.Equal(dir.File("tool.exe"), ExecutableLocator.FindOnPath("tool.exe", "bad|<>dir;;\"" + dir.Path + "\""));
    }

    [Fact]
    public void PowerShell_prefers_pwsh()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("pwsh.exe"), "");

        Assert.Equal(dir.File("pwsh.exe"), ExecutableLocator.PowerShell(dir.Path));
    }

    [Fact]
    public void PowerShell_falls_back_to_windows_powershell()
    {
        using var dir = new TempDir();

        Assert.Equal("powershell.exe", ExecutableLocator.PowerShell(dir.Path));
    }

    [Fact]
    public void Empty_quoted_and_relative_entries_are_skipped()
    {
        // A relative PATH entry resolves against the working directory, so put the tool in a
        // uniquely named folder relative to it. The process-wide current directory is never changed.
        var relativeDir = "locator-" + Guid.NewGuid().ToString("N");
        var absoluteDir = Path.GetFullPath(relativeDir);
        Directory.CreateDirectory(absoluteDir);
        try
        {
            File.WriteAllText(Path.Combine(absoluteDir, "tool.exe"), "");

            Assert.Null(ExecutableLocator.FindOnPath("tool.exe", "\"\""));
            Assert.Null(ExecutableLocator.FindOnPath("tool.exe", "\" \";" + relativeDir));
            Assert.Null(ExecutableLocator.FindOnPath("tool.exe", "\"\";;" + relativeDir));
        }
        finally
        {
            Directory.Delete(absoluteDir, recursive: true);
        }
    }
}
