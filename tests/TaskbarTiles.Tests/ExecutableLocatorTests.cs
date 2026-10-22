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
    public void Empty_quoted_and_relative_entries_never_probe_the_working_directory()
    {
        using var cwd = new TempDir();
        File.WriteAllText(cwd.File("tool.exe"), "");
        var saved = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = cwd.Path;

            Assert.Null(ExecutableLocator.FindOnPath("tool.exe", "\"\""));
            Assert.Null(ExecutableLocator.FindOnPath("tool.exe", "\" \";."));
            Assert.Null(ExecutableLocator.FindOnPath("tool.exe", "\"\";;relative\\dir"));
        }
        finally
        {
            Environment.CurrentDirectory = saved;
        }
    }
}
