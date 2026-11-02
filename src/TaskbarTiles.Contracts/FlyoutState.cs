namespace TaskbarTiles.Contracts;

/// <summary>A popup line; with <paramref name="Action"/> it is clickable and sends that action id to the connector.</summary>
public sealed record FlyoutRow(string Text, string? Value = null, TileIcon? Icon = null, string? Action = null);

public sealed record FlyoutAction(string Id, string Label, bool Primary = false);

public sealed record FlyoutState(
    string Title,
    string? Subtitle,
    IReadOnlyList<FlyoutRow> Rows,
    IReadOnlyList<FlyoutAction> Actions,
    byte[]? Image = null,
    string? Tint = null); // "#RRGGBB" the popup is tinted with; null keeps the plain backdrop
