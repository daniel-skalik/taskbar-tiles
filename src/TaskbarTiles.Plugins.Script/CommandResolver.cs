namespace TaskbarTiles.Plugins.Script;

/// <summary>A process to start: what ProcessStartInfo needs.</summary>
public sealed record ScriptCommand(string FileName, string Arguments, string WorkingDirectory);

/// <summary>
/// Turns a hook's "run" into a process. The first token is the script (quote it if it has spaces), resolved
/// against the config folder; .ps1 runs via PowerShell, .py via python, other existing files and plain
/// command lines via cmd.exe. The rest of the line is passed on unchanged.
/// </summary>
public sealed class CommandResolver(string configDirectory, string powerShell, string python = "python")
{
    const string Shell = "cmd.exe";

    public ScriptCommand Resolve(string run)
    {
        var line = run.Trim();
        var (first, rest) = SplitFirst(line);
        string path;
        try
        {
            path = Path.IsPathFullyQualified(first) ? first : Path.GetFullPath(Path.Combine(configDirectory, RelativePart(first)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(Shell, $"/d /s /c \"{line}\"", configDirectory); // the first token is no path at all
        }
        var tail = rest.Length == 0 ? "" : " " + rest;

        return Path.GetExtension(first).ToLowerInvariant() switch
        {
            ".ps1" => new(powerShell, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File {Quote(path)}{tail}", configDirectory),
            ".py" => new(python, $"-u {Quote(path)}{tail}", configDirectory),
            _ when File.Exists(path) => new(Shell, $"/d /s /c \"{Quote(path)}{tail}\"", configDirectory),
            _ => new(Shell, $"/d /s /c \"{line}\"", configDirectory),
        };
    }

    /// <summary>Drops a drive ("C:x") and leading separators so a rooted but not fully qualified path stays under the config folder.</summary>
    static string RelativePart(string path)
    {
        if (path.Length >= 2 && path[1] == ':') path = path[2..];
        return path.TrimStart('\\', '/');
    }

    internal static (string Script, string Remainder) SplitFirst(string run)
    {
        if (run.StartsWith('"'))
        {
            var end = run.IndexOf('"', 1);
            return end < 0 ? (run[1..], "") : (run[1..end], run[(end + 1)..].Trim());
        }
        var space = run.IndexOfAny([' ', '\t']);
        return space < 0 ? (run, "") : (run[..space], run[(space + 1)..].Trim());
    }

    static string Quote(string path) => "\"" + path + "\"";
}
