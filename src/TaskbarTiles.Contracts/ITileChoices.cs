namespace TaskbarTiles.Contracts;

/// <summary>
/// Optional for a connector: what a "pick" field of the tile's settings dialog offers right now, such as the devices it
/// has seen. The host asks when the dialog opens, with the same timeout as other calls; values already saved are added.
/// </summary>
public interface ITileChoices
{
    Task<IReadOnlyList<string>> GetChoicesAsync(string key, CancellationToken ct);
}
