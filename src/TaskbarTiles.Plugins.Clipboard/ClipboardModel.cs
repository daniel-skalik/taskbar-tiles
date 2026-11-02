namespace TaskbarTiles.Plugins.Clipboard;

public enum ClipKind { Text, Image, Files }

/// <summary>
/// What was copied. <paramref name="Text"/> is the content for text, else a label ("Image 800×600", "3 files: a.txt");
/// <paramref name="Detail"/> is the tile's subtitle for non-text ("Image 800×600", "3 files"), null for text.
/// </summary>
public sealed record ClipItem(ClipKind Kind, string Text, string? Detail = null);

/// <summary>One history row. The Id stays with the entry for as long as it lives, so a popup row still points at it after the list changes.</summary>
public sealed record ClipEntry(long Id, ClipItem Item, DateTime Time);

/// <summary>
/// The outcome of reading the clipboard. <paramref name="Retry"/>: it could not be opened (another app holds it), so
/// look again on the next poll. <paramref name="Excluded"/>: the owner asked not to be recorded; no content was read.
/// Neither, with a null item: empty, or a format we do not keep.
/// </summary>
public readonly record struct ClipRead(ClipItem? Item, bool Excluded = false, bool Retry = false);

/// <summary>The clipboard behind a seam, so tests need no real one.</summary>
public interface IClipboardAccess
{
    /// <summary>Changes whenever the clipboard content does; cheap, needs no clipboard lock.</summary>
    uint SequenceNumber { get; }

    ClipRead Read();

    /// <summary>Puts text on the clipboard; returns the sequence number it now has, or null when it could not.</summary>
    uint? WriteText(string text);
}

/// <summary>Newest first, capped, in memory only. Not thread-safe on its own; the connector's gate serializes it.</summary>
public sealed class ClipHistory(int capacity)
{
    readonly List<ClipEntry> _entries = [];
    long _nextId = 1;

    public int Capacity { get; } = capacity;

    /// <summary>
    /// Adds a copy on top. Text equal to an existing entry moves that entry up (with the new time) instead of
    /// duplicating it. Images and files never merge: two images of one size can still be different pictures.
    /// </summary>
    public ClipEntry Add(ClipItem item, DateTime time)
    {
        if (item.Kind == ClipKind.Text)
        {
            var same = _entries.FindIndex(e => e.Item.Kind == ClipKind.Text && e.Item.Text == item.Text);
            if (same >= 0)
            {
                var moved = _entries[same] with { Time = time };
                _entries.RemoveAt(same);
                _entries.Insert(0, moved);
                return moved;
            }
        }
        var entry = new ClipEntry(_nextId++, item, time);
        _entries.Insert(0, entry);
        if (_entries.Count > Capacity) _entries.RemoveRange(Capacity, _entries.Count - Capacity);
        return entry;
    }

    public ClipEntry? Find(long id) => _entries.FirstOrDefault(e => e.Id == id);

    public void Clear() => _entries.Clear();

    public IReadOnlyList<ClipEntry> Snapshot() => _entries.ToArray();
}

/// <summary>Password managers mark their copies with two registered formats; those must never be recorded.</summary>
public static class ClipboardPrivacy
{
    public const string ExcludeFormat = "ExcludeClipboardContentFromMonitorProcessing";
    public const string CanIncludeFormat = "CanIncludeInClipboardHistory";

    /// <summary>
    /// The exclude format being present is enough. The include format carries a DWORD, where 0 means "keep out of
    /// history"; a missing or unreadable value does not exclude.
    /// </summary>
    public static bool IsExcluded(bool hasExcludeFormat, uint? canIncludeValue) => hasExcludeFormat || canIncludeValue == 0;
}
