using TaskbarTiles.Plugins.Script;

namespace TaskbarTiles.Tests;

public class HookResultsTests
{
    static readonly TimeSpan Limit = TimeSpan.FromSeconds(2);

    [Fact]
    public void Json_output_becomes_the_state()
    {
        var o = HookResults.ForTile("interval", new ScriptResult(0, """{ "title": "x" }""", ""), Limit);

        Assert.False(o.Failed);
        Assert.Equal("x", o.Value!.Title);
    }

    [Fact]
    public void Empty_output_is_nothing()
    {
        var o = HookResults.ForTile("click", new ScriptResult(0, "\n", "a log line on stderr"), Limit);

        Assert.False(o.Failed);
        Assert.Null(o.Value);
    }

    [Fact]
    public void Non_zero_exit_fails_with_stderr()
    {
        var o = HookResults.ForTile("interval", new ScriptResult(3, """{ "title": "x" }""", "it broke\r\n"), Limit);

        Assert.True(o.Failed);
        Assert.Equal("interval: exited with code 3", o.ErrorMessage);
        Assert.Equal("it broke", o.ErrorDetails);
        Assert.Null(o.Value);
    }

    [Fact]
    public void Non_zero_exit_without_stderr_shows_stdout() =>
        Assert.Equal("plain text", HookResults.ForTile("interval", new ScriptResult(3, "plain text", ""), Limit).ErrorDetails);

    [Fact]
    public void Silent_failure_says_so() =>
        Assert.Equal("(no output)", HookResults.ForTile("interval", new ScriptResult(1, "", " "), Limit).ErrorDetails);

    [Fact]
    public void Timeout_fails()
    {
        var o = HookResults.ForTile("interval", new ScriptResult(-1, "", "partial", TimedOut: true), Limit);

        Assert.Equal("interval: timed out after 2 s", o.ErrorMessage);
        Assert.Equal("partial", o.ErrorDetails);
    }

    [Fact]
    public void Start_error_fails()
    {
        var o = HookResults.ForTile("click", new ScriptResult(-1, "", "", StartError: "python: not found"), Limit);

        Assert.Equal("click: could not start the script", o.ErrorMessage);
        Assert.Equal("python: not found", o.ErrorDetails);
    }

    [Fact]
    public void Invalid_output_fails_with_the_parse_error_and_the_output()
    {
        var o = HookResults.ForTile("interval", new ScriptResult(0, "oops", ""), Limit);

        Assert.Equal("interval: invalid output", o.ErrorMessage);
        Assert.StartsWith("Invalid JSON:", o.ErrorDetails);
        Assert.EndsWith("oops", o.ErrorDetails);
    }

    [Fact]
    public void Invalid_output_details_include_stderr()
    {
        var o = HookResults.ForTile("interval", new ScriptResult(0, "oops", "traceback: boom\r\n"), Limit);

        Assert.Equal("interval: invalid output", o.ErrorMessage);
        Assert.Contains("oops", o.ErrorDetails);
        Assert.EndsWith("traceback: boom", o.ErrorDetails);
    }

    [Fact]
    public void Warnings_name_the_hook()
    {
        var o = HookResults.ForTile("interval", new ScriptResult(0, """{ "tittle": "x" }""", ""), Limit);

        Assert.StartsWith("interval: Unknown field", Assert.Single(o.Warnings));
    }

    [Fact]
    public void Long_details_keep_the_end()
    {
        var o = HookResults.ForTile("interval", new ScriptResult(1, "", new string('a', 5000) + "END"), Limit);

        Assert.Equal(HookResults.MaxDetails + 1, o.ErrorDetails.Length);
        Assert.StartsWith("…", o.ErrorDetails);
        Assert.EndsWith("END", o.ErrorDetails);
    }

    [Fact]
    public void Flyout_output_becomes_the_popup() =>
        Assert.Equal("t", HookResults.ForFlyout("flyout", new ScriptResult(0, """{ "title": "t" }""", ""), Limit).Value!.Title);
}
