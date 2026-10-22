namespace TaskbarTiles.Tests;

public class ScaffoldTests
{
    [Fact]
    public void Host_assembly_is_named_TaskbarTiles() =>
        Assert.Equal("TaskbarTiles", typeof(TaskbarTiles.Host.App).Assembly.GetName().Name);
}
