using System.Text.Json;
using TaskbarTiles.Plugins.Script;

namespace TaskbarTiles.Tests;

public class ScriptSettingsTests
{
    static ScriptSettings Parse(string json) => ScriptSettings.Parse(JsonDocument.Parse(json).RootElement.Clone());

    [Fact]
    public void Parses_every_hook_kind()
    {
        var s = Parse("""
            { "hooks": {
                "interval": { "every": "30s", "run": "scripts/a.ps1" },
                "watch": { "run": "scripts/w.ps1" },
                "click": { "run": "scripts/c.ps1" },
                "flyout": { "run": "scripts/f.ps1", "timeout": "5s" },
                "startup": { "run": "scripts/s.ps1" },
                "resume": { "run": "scripts/r.ps1" },
                "action:rerun": { "run": "scripts/rerun.ps1 --fast" } } }
            """);

        Assert.Empty(s.Errors);
        Assert.Equal(7, s.Hooks.Count);
        Assert.Equal(TimeSpan.FromSeconds(30), s.Hook("interval")!.Every);
        Assert.Equal("scripts/a.ps1", s.Hook("interval")!.Run);
        Assert.Equal("scripts/w.ps1", s.Hook("watch")!.Run);
        Assert.Null(s.Hook("watch")!.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(5), s.Hook("flyout")!.Timeout);
        Assert.Equal("scripts/rerun.ps1 --fast", s.Action("rerun")!.Run);
        Assert.Null(s.Action("other"));
        Assert.Null(s.Hook("nope"));
    }

    [Fact]
    public void A_string_is_shorthand_for_run()
    {
        var s = Parse("""{ "hooks": { "click": "scripts/c.ps1", "action:go": " scripts/go.ps1 " } }""");

        Assert.Empty(s.Errors);
        Assert.Equal(new HookSpec("scripts/c.ps1"), s.Hook("click"));
        Assert.Equal("scripts/go.ps1", s.Action("go")!.Run);
    }

    [Theory]
    [InlineData("""{ }""", "settings.hooks")]
    [InlineData("""{ "hooks": [] }""", "settings.hooks")]
    [InlineData("""{ "hooks": { } }""", "empty")]
    [InlineData("""{ "hooks": { "interval": { "run": "a.ps1" } } }""", "every")]
    [InlineData("""{ "hooks": { "interval": { "every": "soon", "run": "a.ps1" } } }""", "every")]
    [InlineData("""{ "hooks": { "interval": { "every": "0s", "run": "a.ps1" } } }""", "every")]
    [InlineData("""{ "hooks": { "interval": "a.ps1" } }""", "every")]
    [InlineData("""{ "hooks": { "click": { } } }""", "\"run\"")]
    [InlineData("""{ "hooks": { "click": "  " } }""", "\"run\"")]
    [InlineData("""{ "hooks": { "click": 5 } }""", "must be an object")]
    [InlineData("""{ "hooks": { "hover": { "run": "a.ps1" } } }""", "Unknown hook")]
    [InlineData("""{ "hooks": { "action:": { "run": "a.ps1" } } }""", "needs an id")]
    [InlineData("""{ "hooks": { "click": { "run": "a.ps1", "timeout": "x" } } }""", "timeout")]
    public void Invalid_settings_explain_themselves(string json, string expected)
    {
        var s = Parse(json);

        Assert.Contains(s.Errors, e => e.Contains(expected));
    }

    [Fact]
    public void Valid_hooks_are_kept_next_to_errors()
    {
        var s = Parse("""{ "hooks": { "click": "c.ps1", "hover": "h.ps1" } }""");

        Assert.Single(s.Errors);
        Assert.NotNull(s.Hook("click"));
    }
}
