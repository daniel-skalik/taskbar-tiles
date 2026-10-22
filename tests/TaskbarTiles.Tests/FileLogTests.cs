using System.IO;
using TaskbarTiles.Host.Runtime;

namespace TaskbarTiles.Tests;

public class FileLogTests
{
    [Fact]
    public void Same_directory_and_name_return_the_same_instance()
    {
        using var dir = new TempDir();

        Assert.Same(FileLog.For(dir.Path, "host"), FileLog.For(dir.Path, "host"));
        Assert.NotSame(FileLog.For(dir.Path, "host"), FileLog.For(dir.Path, "tile-host"));
    }

    [Theory]
    [InlineData("..\\x")]
    [InlineData("../x")]
    [InlineData("a/b\\c")]
    [InlineData("..")]
    [InlineData("we:ird*name?")]
    public void Hostile_names_stay_inside_the_logs_directory(string name)
    {
        using var dir = new TempDir();
        var logsDir = Path.Combine(dir.Path, "logs");

        FileLog.For(logsDir, name).Info("hello");

        var file = Assert.Single(Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories));
        Assert.Equal(Path.GetFullPath(logsDir), Path.GetDirectoryName(file));
        Assert.Contains("hello", File.ReadAllText(file));
    }

    [Fact]
    public void Concurrent_appends_through_repeated_lookups_lose_no_lines()
    {
        using var dir = new TempDir();

        Parallel.For(0, 200, i => FileLog.For(dir.Path, "shared").Info("line " + i));

        Assert.Equal(200, File.ReadAllLines(Path.Combine(dir.Path, "shared.log")).Length);
    }
}
