using System.Globalization;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Plugins.Script;

/// <summary>What a hook run means for the tile: a value, nothing (Value and ErrorMessage both null), or a failure.</summary>
public sealed record HookOutcome<T>(T? Value, string? ErrorMessage, string ErrorDetails, IReadOnlyList<string> Warnings) where T : class
{
    public bool Failed => ErrorMessage is not null;
}

public static class HookResults
{
    /// <summary>Longest details text kept for the popup (the end is kept: that is where errors are).</summary>
    public const int MaxDetails = 4000;

    public static HookOutcome<TileState> ForTile(string hook, ScriptResult result, TimeSpan timeout) =>
        Interpret(hook, result, timeout, ScriptOutput.ParseTile);

    public static HookOutcome<FlyoutState> ForFlyout(string hook, ScriptResult result, TimeSpan timeout) =>
        Interpret(hook, result, timeout, ScriptOutput.ParseFlyout);

    /// <summary>What to show for a failed run: stderr, else stdout, else "(no output)".</summary>
    public static string Details(ScriptResult result)
    {
        var text = !string.IsNullOrWhiteSpace(result.StdErr) ? result.StdErr
            : !string.IsNullOrWhiteSpace(result.StdOut) ? result.StdOut
            : "(no output)";
        return Shorten(text.Trim());
    }

    static HookOutcome<T> Interpret<T>(string hook, ScriptResult result, TimeSpan timeout, Func<string?, ParseResult<T>> parse) where T : class
    {
        if (result.StartError is not null) return Fail<T>($"{hook}: could not start the script", result.StartError);
        if (result.TimedOut) return Fail<T>($"{hook}: timed out after {timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s", Details(result));
        if (result.ExitCode != 0) return Fail<T>($"{hook}: exited with code {result.ExitCode}", Details(result));

        var parsed = parse(result.StdOut);
        var warnings = parsed.Warnings.Select(w => $"{hook}: {w}").ToList();
        if (parsed.Error is not null)
        {
            var details = parsed.Error + "\n" + result.StdOut.Trim();
            if (!string.IsNullOrWhiteSpace(result.StdErr)) details += "\n" + result.StdErr.Trim();
            return new(null, $"{hook}: invalid output", Shorten(details), warnings);
        }
        return new(parsed.Value, null, "", warnings);
    }

    static HookOutcome<T> Fail<T>(string message, string details) where T : class => new(null, message, Shorten(details), []);

    static string Shorten(string text) => text.Length <= MaxDetails ? text : "…" + text[^MaxDetails..];
}
