namespace TaskbarTiles.Contracts;

/// <summary>
/// Optional for a connector: what a "pick" field of the tile's settings dialog offers right now, such as the devices it
/// has seen, or what a "text" field suggests, such as the apps playing media. The host asks when the dialog opens, with
/// the same timeout as other calls; the field's fixed choices and values already saved are added.
/// </summary>
public interface ITileChoices
{
    Task<IReadOnlyList<string>> GetChoicesAsync(string key, CancellationToken ct);
}
