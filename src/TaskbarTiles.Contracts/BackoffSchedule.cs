namespace TaskbarTiles.Contracts;

/// <summary>
/// Delays before restarting something that keeps failing: 2 s, 10 s, 1 min, then every 5 min.
/// The host uses it for failing connectors; plugins can use it for what they restart themselves.
/// Callers reset their failure count after a success.
/// </summary>
public sealed class BackoffSchedule
{
    readonly TimeSpan[] _steps;

    public BackoffSchedule(IReadOnlyList<TimeSpan> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0) throw new ArgumentException("A backoff schedule needs at least one step.", nameof(steps));
        if (steps.Any(s => s < TimeSpan.Zero)) throw new ArgumentOutOfRangeException(nameof(steps), "Backoff steps cannot be negative.");
        _steps = steps.ToArray();
    }

    public static BackoffSchedule Default { get; } = new(
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)]);

    public IReadOnlyList<TimeSpan> Steps => _steps;

    /// <summary>The delay after a failure, given how many failures came right before it (0 for the first one). The last step repeats.</summary>
    public TimeSpan Delay(int previousFailures) => _steps[Math.Clamp(previousFailures, 0, _steps.Length - 1)];
}
