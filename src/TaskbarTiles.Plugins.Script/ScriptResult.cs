namespace TaskbarTiles.Plugins.Script;

/// <summary>How a script run ended. StartError is set when the process could not be started at all.</summary>
public sealed record ScriptResult(int ExitCode, string StdOut, string StdErr, bool TimedOut = false, string? StartError = null);
