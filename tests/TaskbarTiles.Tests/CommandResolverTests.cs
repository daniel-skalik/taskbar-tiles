using System.IO;
using TaskbarTiles.Plugins.Script;

namespace TaskbarTiles.Tests;

public class CommandResolverTests
{
    const string ConfigDir = @"C:\cfg";
    static readonly CommandResolver Resolver = new(ConfigDir, "pwsh.exe");

    [Fact]
    public void Relative_ps1_runs_with_powershell_from_the_config_folder()
    {
        var c = Resolver.Resolve("scripts/ci.ps1");

        Assert.Equal("pwsh.exe", c.FileName);
        Assert.Equal(@"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""C:\cfg\scripts\ci.ps1""", c.Arguments);
        Assert.Equal(ConfigDir, c.WorkingDirectory);
    }

    [Fact]
    public void Arguments_after_the_script_are_passed_on() =>
        Assert.EndsWith(@"-File ""C:\cfg\scripts\ci.ps1"" -Branch main", Resolver.Resolve("scripts/ci.ps1 -Branch main").Arguments);

    [Fact]
    public void Quoted_path_may_contain_spaces_and_extension_case_does_not_matter() =>
        Assert.EndsWith(@"-File ""C:\cfg\my scripts\x.PS1"" a b", Resolver.Resolve("\"my scripts/x.PS1\" a b").Arguments);

    [Fact]
    public void Unclosed_quote_takes_the_rest_as_the_path() =>
        Assert.EndsWith(@"-File ""C:\cfg\scripts\x.ps1""", Resolver.Resolve("\"scripts/x.ps1").Arguments);

    [Fact]
    public void Absolute_path_is_kept() =>
        Assert.EndsWith(@"-File ""D:\tools\x.ps1""", Resolver.Resolve(@"D:\tools\x.ps1").Arguments);

    [Fact]
    public void Python_runs_unbuffered()
    {
        var c = Resolver.Resolve("scripts/w.py --once");

        Assert.Equal("python", c.FileName);
        Assert.Equal(@"-u ""C:\cfg\scripts\w.py"" --once", c.Arguments);
    }

    [Fact]
    public void Existing_other_file_runs_through_cmd()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("x.cmd"), "@echo {}");

        var c = new CommandResolver(dir.Path, "pwsh.exe").Resolve("x.cmd arg");

        Assert.Equal("cmd.exe", c.FileName);
        Assert.Equal($"/d /s /c \"\"{dir.File("x.cmd")}\" arg\"", c.Arguments);
        Assert.Equal(dir.Path, c.WorkingDirectory);
    }

    [Fact]
    public void Anything_else_is_a_command_line_for_cmd()
    {
        var c = Resolver.Resolve("  echo {\"title\":\"hi\"}  ");

        Assert.Equal("cmd.exe", c.FileName);
        Assert.Equal("/d /s /c \"echo {\"title\":\"hi\"}\"", c.Arguments);
    }
}
