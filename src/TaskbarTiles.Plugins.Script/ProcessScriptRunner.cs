using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace TaskbarTiles.Plugins.Script;

/// <summary>Runs scripts as hidden child processes with redirected, UTF-8 decoded output.</summary>
public sealed class ProcessScriptRunner : IScriptRunner
{
    const int MaxStdOut = 1024 * 1024;
    const int MaxStdErr = 16 * 1024;
    const int MaxLine = 1024 * 1024;
    const double MaxTimeoutMs = uint.MaxValue - 1; // what CancellationTokenSource.CancelAfter can schedule
    const string TruncatedMarker = "(output truncated)";
    static readonly TimeSpan DrainLimit = TimeSpan.FromSeconds(2);

    public async Task<ScriptResult> RunAsync(ScriptCommand command, IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Validate first: an invalid timeout must not throw after the process has started.
        if (timeout != Timeout.InfiniteTimeSpan && (timeout < TimeSpan.Zero || timeout.TotalMilliseconds > MaxTimeoutMs))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        using var process = Start(command, environment, out var startError);
        if (process is null) return StartFailed(startError!);
        var stdout = new BoundedText(MaxStdOut);
        var stderr = new BoundedText(MaxStdErr);
        var pumps = Task.WhenAll(Pump(process.StandardOutput, stdout.AppendLine, stdout.MarkTruncated), Pump(process.StandardError, stderr.AppendLine, stderr.MarkTruncated));

        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await Drain(pumps).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return new ScriptResult(-1, stdout.ToString(), Combine(stderr, stdout), TimedOut: true);
        }

        // On a normal exit, background children the script deliberately left running are not killed (by design).
        await Drain(pumps).ConfigureAwait(false);
        return new ScriptResult(process.ExitCode, stdout.ToString(), Combine(stderr, stdout));
    }

    public async Task<ScriptResult> RunLinesAsync(ScriptCommand command, IReadOnlyDictionary<string, string> environment, Action<string> onLine, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var process = Start(command, environment, out var startError);
        if (process is null) return StartFailed(startError!);
        var stderr = new BoundedText(MaxStdErr);
        var lineCut = new BoundedText(0);
        Exception? callbackError = null;
        void SafeOnLine(string line)
        {
            try { onLine(line); }
            catch (Exception ex) { Interlocked.CompareExchange(ref callbackError, ex, null); }
        }
        var pumps = Task.WhenAll(Pump(process.StandardOutput, SafeOnLine, lineCut.MarkTruncated), Pump(process.StandardError, stderr.AppendLine, stderr.MarkTruncated));
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

        // On a normal exit, background children the script deliberately left running are not killed (by design).
        await Drain(pumps).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var error = callbackError is null ? null : $"The line handler failed: {callbackError.Message}";
        return new ScriptResult(process.ExitCode, "", Combine(stderr, lineCut), StartError: error);
    }

    /// <summary>The stderr text, with a marker when either captured stream was cut short.</summary>
    static string Combine(BoundedText stderr, BoundedText other)
    {
        var text = stderr.ToString();
        if (!stderr.Truncated && !other.Truncated) return text;
        return text.Length > 0 ? text + "\n" + TruncatedMarker : TruncatedMarker;
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

    /// <summary>Reads in chunks and splits lines itself, so memory stays bounded however little newline the script prints.</summary>
    static async Task Pump(StreamReader reader, Action<string> onLine, Action onTruncated)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        var cut = false;
        var skipLf = false;
        void Emit()
        {
            if (cut) onTruncated();
            onLine(line.ToString());
            line.Clear();
            cut = false;
        }
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    var c = buffer[i];
                    if (skipLf)
                    {
                        skipLf = false;
                        if (c == '\n') continue;
                    }
                    if (c is '\n' or '\r')
                    {
                        skipLf = c == '\r';
                        Emit();
                    }
                    else if (line.Length < MaxLine) line.Append(c);
                    else cut = true;
                }
            }
            if (line.Length > 0 || cut) Emit();
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
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException) { }
    }

    /// <summary>Thread-safe line buffer that keeps the first <c>limit</c> characters and remembers when it dropped the rest.</summary>
    sealed class BoundedText(int limit)
    {
        readonly StringBuilder _text = new();
        bool _truncated;
        bool _full;

        public bool Truncated { get { lock (_text) return _truncated; } }

        public void MarkTruncated()
        {
            lock (_text) _truncated = true;
        }

        public void AppendLine(string line)
        {
            lock (_text)
            {
                if (_full) return;
                if (_text.Length > 0) line = "\n" + line;
                var room = limit - _text.Length;
                if (line.Length > room)
                {
                    _text.Append(line, 0, room);
                    _truncated = _full = true;
                    return;
                }
                _text.Append(line);
            }
        }

        public override string ToString()
        {
            lock (_text) return _text.ToString();
        }
    }
}
