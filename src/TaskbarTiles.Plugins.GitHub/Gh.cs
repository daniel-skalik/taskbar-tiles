using System.Diagnostics;
using System.Text;

namespace TaskbarTiles.Plugins.GitHub;

/// <summary>What one gh run printed and how it ended.</summary>
public sealed record GhResult(int ExitCode, string Output, string Errors)
{
    /// <summary>The first non-empty line of what gh wrote to stderr (or stdout), for a short message.</summary>
    public string FirstLine => (Errors + "\n" + Output).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? $"gh exited with code {ExitCode}";

    /// <summary>gh says it has no signed-in account (or GitHub refused its token).</summary>
    public bool NotSignedIn => ExitCode != 0 &&
        (Errors.Contains("auth login", StringComparison.Ordinal) || Errors.Contains("not logged", StringComparison.OrdinalIgnoreCase)
         || Errors.Contains("Bad credentials", StringComparison.Ordinal) || Errors.Contains("HTTP 401", StringComparison.Ordinal));
}

/// <summary>The GitHub CLI. Tests stand in for it.</summary>
public interface IGh
{
    /// <summary>Whether gh is installed. Looked up on every call: it may be installed while the app runs.</summary>
    bool Installed { get; }

    Task<GhResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct);
}

/// <summary>
/// Runs gh.exe with its own sign-in: this plugin never reads, stores or logs a token. gh is found on PATH, or in the
/// folders its installers use, since the app's PATH is the one it started with and gh may have been installed since.
/// </summary>
public sealed class GhCli : IGh
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public bool Installed => Find() is not null;

    public static string? Find()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), "gh.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { } // a PATH entry with characters a path cannot have
        }
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        ];
        return roots.Where(r => r.Length > 0).Select(r => Path.Combine(r, "GitHub CLI", "gh.exe")).FirstOrDefault(File.Exists);
    }

    public async Task<GhResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var exe = Find() ?? throw new InvalidOperationException("The GitHub CLI (gh) is not installed.");
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in arguments) start.ArgumentList.Add(a);
        start.Environment["GH_PROMPT_DISABLED"] = "1"; // never wait for an answer nobody can give
        start.Environment["NO_COLOR"] = "1";

        using var process = Process.Start(start) ?? throw new InvalidOperationException("gh did not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return new GhResult(process.ExitCode, await output, await errors);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } // already gone
            if (ct.IsCancellationRequested) throw;
            return new GhResult(-1, "", $"gh did not answer within {Timeout.TotalSeconds:0} s");
        }
    }
}
