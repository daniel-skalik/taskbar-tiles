using System.Text.Json;
using TaskbarTiles.Contracts;

namespace TaskbarTiles.Plugins.Clipboard;

/// <summary>
/// The last thing copied on the tile, and the recent copies in the popup, where a text one can be copied again.
/// Setting "history": how many copies to keep, 1..50, default 15. Copies live in memory only: never written to the log,
/// and to disk only when you press Save. Anything a password manager marks private is not recorded at all.
/// </summary>
[TileConnector("clipboard", Width = 200)]
public sealed class ClipboardConnector : ITileConnector
{
    const int DefaultHistory = 15, MinHistory = 1, MaxHistory = 50;
    static readonly TimeSpan DefaultPoll = TimeSpan.FromMilliseconds(400);

    readonly IClipboardAccess _clipboard;
    readonly TimeSpan _poll;
    readonly Func<DateTime> _now;
    readonly SemaphoreSlim _gate = new(1, 1); // one clipboard operation at a time, whoever asks
    readonly CancellationTokenSource _stop = new();
    readonly CancellationToken _stopping;
    volatile bool _disposed;
    ITileContext? _ctx;
    ClipHistory _history = new(DefaultHistory);
    volatile IReadOnlyList<ClipEntry> _last = [];
    volatile FlyoutRow? _saveRow; // the last Save's outcome, in memory only
    volatile string? _savedPath;  // written under the gate, read by ShowSaved without it
    uint _seenSequence; // only touched under the gate
    Task? _loop;

    public ClipboardConnector() : this(new Win32Clipboard(), DefaultPoll, () => DateTime.Now) { }

    public ClipboardConnector(IClipboardAccess clipboard, TimeSpan poll, Func<DateTime> now)
    {
        _clipboard = clipboard;
        _poll = poll;
        _now = now;
        _stopping = _stop.Token; // kept so a late StartAsync never touches the disposed source
    }

    public async Task StartAsync(ITileContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        _history = new ClipHistory(HistorySize(ctx.Settings, ctx.Log));
        ctx.Update(ClipboardView.Tile([]));
        await CheckAsync(_stopping); // whatever is on the clipboard now counts as the latest copy
        if (!_disposed) _loop = LoopAsync(_stopping);
    }

    /// <summary>"history" from settings, clamped to 1..50; a bad value is logged and the default used.</summary>
    public static int HistorySize(JsonElement settings, ITileLog log)
    {
        if (settings.ValueKind != JsonValueKind.Object || !settings.TryGetProperty("history", out var value)) return DefaultHistory;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number))
        {
            var rounded = (int)Math.Round(Math.Clamp(number, MinHistory, MaxHistory));
            if (rounded != number) log.Warn($"\"history\" must be from {MinHistory} to {MaxHistory}; using {rounded}.");
            return rounded;
        }
        log.Warn($"\"history\" must be a number; using {DefaultHistory}.");
        return DefaultHistory;
    }

    async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_poll);
        try
        {
            while (await timer.WaitForNextTickAsync(ct)) await CheckAsync(ct);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Reads the clipboard if its sequence number moved. A failure is logged without content and tried again next tick.</summary>
    async Task CheckAsync(CancellationToken ct)
    {
        var ctx = _ctx;
        if (ctx is null || _disposed) return;
        try { await _gate.WaitAsync(ct); }
        catch (OperationCanceledException) { return; }
        try
        {
            if (_disposed) return;
            var sequence = _clipboard.SequenceNumber;
            if (sequence == _seenSequence) return;

            ClipRead read;
            try { read = await Task.Run(_clipboard.Read, CancellationToken.None); }
            catch (Exception ex)
            {
                ctx.Log.Warn($"Could not read the clipboard: {ex.Message}");
                return;
            }
            if (read.Retry) return; // held by another app; _seenSequence stays, so the next tick looks again
            _seenSequence = sequence;
            if (read.Excluded || read.Item is null) return;

            _history.Add(read.Item, _now());
            Publish(ctx);
        }
        finally { _gate.Release(); }
    }

    void Publish(ITileContext ctx)
    {
        _last = _history.Snapshot();
        ctx.Update(ClipboardView.Tile(_last));
    }

    public async Task OnActionAsync(string actionId, CancellationToken ct)
    {
        var ctx = _ctx;
        if (ctx is null || _disposed) return;
        if (actionId == ClipboardSave.SaveAction) { await SaveAsync(ctx, ct); return; }
        if (actionId == ClipboardSave.ShowSavedAction) { ShowSaved(ctx); return; }
        if (actionId != ClipboardView.ClearAction && ClipboardView.CopyId(actionId) is not { } id) return;

        try { await _gate.WaitAsync(ct); }
        catch (OperationCanceledException) { return; }
        try
        {
            if (_disposed) return;
            if (actionId == ClipboardView.ClearAction)
            {
                _history.Clear();
                _saveRow = null; _savedPath = null;
                Publish(ctx);
                ctx.Log.Info("Clipboard history cleared.");
                return;
            }

            var entry = _history.Find(ClipboardView.CopyId(actionId)!.Value);
            if (entry is null || entry.Item.Kind != ClipKind.Text) return; // gone since the popup was drawn
            uint? written;
            try { written = await Task.Run(() => _clipboard.WriteText(entry.Item.Text), CancellationToken.None); }
            catch (Exception ex)
            {
                ctx.Log.Warn($"Could not write to the clipboard: {ex.Message}");
                return;
            }
            if (written is not { } sequence)
            {
                ctx.Log.Warn("Could not write to the clipboard; it stayed busy.");
                return;
            }
            // Our own write must not be read back as a new copy; the entry just moves to the top.
            _seenSequence = sequence;
            _history.Add(entry.Item, _now());
            Publish(ctx);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Writes what is on the clipboard now into the save folder: an image as PNG (it wins over text), text as UTF-8.
    /// The outcome becomes the popup's last row. Content never reaches the log.
    /// </summary>
    async Task SaveAsync(ITileContext ctx, CancellationToken ct)
    {
        try { await _gate.WaitAsync(ct); }
        catch (OperationCanceledException) { return; }
        try
        {
            if (_disposed) return;
            ClipSaveRead read;
            try { read = await Task.Run(_clipboard.ReadForSave, CancellationToken.None); }
            catch (Exception ex)
            {
                ctx.Log.Warn($"Could not read the clipboard to save it: {ex.Message}");
                _saveRow = new FlyoutRow("Could not save: the clipboard could not be read");
                return;
            }
            if (read.Retry)
            {
                ctx.Log.Warn("Could not save the clipboard: another app holds it.");
                _saveRow = new FlyoutRow("Could not save: another app holds the clipboard");
                return;
            }
            if (read.Excluded || read.Files) { _saveRow = new FlyoutRow("Nothing to save"); return; }
            var bmp = read.Dib is { } dib ? ClipboardSave.BmpFromDib(dib) : null;
            // An image that cannot be saved says so rather than quietly saving the text next to it.
            var problem = read.ImageTooLarge ? "the image is too large"
                : read.Dib is not null && bmp is null ? "the image format is not supported"
                : bmp is null && read.TextTooLarge ? "the text is too large"
                : null;
            if (problem is not null)
            {
                ctx.Log.Warn($"Could not save the clipboard: {problem}.");
                _saveRow = new FlyoutRow($"Could not save: {problem}");
                return;
            }
            if (bmp is null && string.IsNullOrWhiteSpace(read.Text)) { _saveRow = new FlyoutRow("Nothing to save"); return; }

            try
            {
                var folder = ClipboardSave.Folder(ctx.Settings);
                Directory.CreateDirectory(folder);
                var name = ClipboardSave.FileName(_now(), bmp is not null ? ".png" : ".txt", n => File.Exists(Path.Combine(folder, n)));
                var path = Path.Combine(folder, name);
                if (bmp is not null) await File.WriteAllBytesAsync(path, await PngEncoder.FromBmpAsync(bmp), CancellationToken.None);
                else await File.WriteAllTextAsync(path, read.Text, new System.Text.UTF8Encoding(false), CancellationToken.None);
                _savedPath = path;
                _saveRow = new FlyoutRow($"Saved {name}", Action: ClipboardSave.ShowSavedAction);
                ctx.Log.Info($"Clipboard saved to {path}.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ctx.Log.Warn($"Could not save the clipboard: {ex.Message}");
                _saveRow = new FlyoutRow($"Could not save: {ex.Message}");
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Explorer with the last saved file selected.</summary>
    void ShowSaved(ITileContext ctx)
    {
        if (_savedPath is not { } path || !File.Exists(path)) return;
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Exception ex) { ctx.Log.Warn($"Could not open Explorer: {ex.Message}"); }
    }

    public Task<FlyoutState?> GetFlyoutAsync(CancellationToken ct) => Task.FromResult<FlyoutState?>(ClipboardView.Flyout(_last, _saveRow));

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        if (_loop is not null) await _loop;
        await _gate.WaitAsync(); // a read or write in progress finishes first
        _gate.Release();
        _stop.Dispose();
    }
}
