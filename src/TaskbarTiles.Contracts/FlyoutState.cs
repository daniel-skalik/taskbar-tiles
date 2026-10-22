namespace TaskbarTiles.Contracts;

public sealed record FlyoutRow(string Text, string? Value = null, TileIcon? Icon = null);

public sealed record FlyoutAction(string Id, string Label, bool Primary = false);

public sealed record FlyoutState(
    string Title,
    string? Subtitle,
    IReadOnlyList<FlyoutRow> Rows,
    IReadOnlyList<FlyoutAction> Actions,
    byte[]? Image = null);
