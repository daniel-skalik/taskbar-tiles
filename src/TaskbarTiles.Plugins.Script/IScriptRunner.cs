namespace TaskbarTiles.Plugins.Script;

/// <summary>Starts script processes. The real one is ProcessScriptRunner; unit tests use a fake.</summary>
public interface IScriptRunner
{
    /// <summary>
    /// Runs to completion and captures the output. Past the timeout the process tree is killed and the result has
    /// TimedOut set. Cancellation kills the process tree and throws OperationCanceledException.
    /// A process that cannot be started (for example a missing interpreter) comes back as a result with StartError set.
    /// </summary>
    Task<ScriptResult> RunAsync(ScriptCommand command, IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// Runs until the process exits, calling onLine for each stdout line as it arrives (StdOut in the result is empty).
    /// Cancellation kills the process tree and throws OperationCanceledException.
    /// A process that cannot be started comes back as a result with StartError set.
    /// </summary>
    Task<ScriptResult> RunLinesAsync(ScriptCommand command, IReadOnlyDictionary<string, string> environment, Action<string> onLine, CancellationToken ct);
}
