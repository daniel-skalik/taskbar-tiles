using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace TaskbarTiles.Plugins.Script;

/// <summary>Runs scripts as hidden child processes with redirected, UTF-8 decoded output.</summary>
public sealed class ProcessScriptRunner : IScriptRunner
{
    const int MaxStdOut = 1024 * 1024;
    const int MaxStdErr = 16 * 1024;
    static readonly TimeSpan DrainLimit = TimeSpan.FromSeconds(2);

    public async Task<ScriptResult> RunAsync(ScriptCommand command, IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var process = Start(command, environment, out var startError);
        if (process is null) return StartFailed(startError!);
        var stdout = new BoundedText(MaxStdOut);
        var stderr = new BoundedText(MaxStdErr);
        var pumps = Task.WhenAll(Pump(process.StandardOutput, stdout.AppendLine), Pump(process.StandardError, stderr.AppendLine));

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await Drain(pumps).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return new ScriptResult(-1, stdout.ToString(), stderr.ToString(), TimedOut: true);
        }

        await Drain(pumps).ConfigureAwait(false);
        return new ScriptResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    public async Task<ScriptResult> RunLinesAsync(ScriptCommand command, IReadOnlyDictionary<string, string> environment, Action<string> onLine, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var process = Start(command, environment, out var startError);
        if (process is null) return StartFailed(startError!);
        var stderr = new BoundedText(MaxStdErr);
        var pumps = Task.WhenAll(Pump(process.StandardOutput, onLine), Pump(process.StandardError, stderr.AppendLine));
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await Drain(pumps).ConfigureAwait(false);
            throw;
        }

        await Drain(pumps).ConfigureAwait(false);
        return new ScriptResult(process.ExitCode, "", stderr.ToString());
    }

    static ScriptResult StartFailed(string error) => new(-1, "", "", StartError: error);

    /// <summary>Starts the process, or returns null with the reason when it cannot be started.</summary>
    static Process? Start(ScriptCommand command, IReadOnlyDictionary<string, string> environment, out string? error)
    {
        var psi = new ProcessStartInfo(command.FileName, command.Arguments)
        {
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Python writes in the console code page when piped unless told otherwise.
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        foreach (var (key, value) in environment) psi.Environment[key] = value;

        Process? process = null;
        try
        {
            process = Process.Start(psi);
            if (process is null)
            {
                error = $"Could not start {command.FileName}.";
                return null;
            }
            process.StandardInput.Close(); // scripts get no input; a script that reads stdin sees end-of-file
            error = null;
            return process;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or ArgumentException or PlatformNotSupportedException)
        {
            if (process is not null)
            {
                Kill(process);
                process.Dispose();
            }
            error = $"Could not start {command.FileName}: {ex.Message}";
            return null;
        }
    }

    static async Task Pump(StreamReader reader, Action<string> onLine)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line) onLine(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The process was killed or disposed while we were reading.
        }
    }

    static async Task Drain(Task pumps)
    {
        try { await pumps.WaitAsync(DrainLimit).ConfigureAwait(false); }
        catch (TimeoutException) { /* a grandchild still holds the pipe open; give up on the rest of the output */ }
    }

    static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }
    }

    /// <summary>Thread-safe line buffer that keeps the last <c>limit</c> characters.</summary>
    sealed class BoundedText(int limit)
    {
        readonly StringBuilder _text = new();

        public void AppendLine(string line)
        {
            lock (_text)
            {
                if (_text.Length > 0) _text.Append('\n');
                _text.Append(line);
                if (_text.Length > limit) _text.Remove(0, _text.Length - limit);
            }
        }

        public override string ToString()
        {
            lock (_text) return _text.ToString();
        }
    }
}
