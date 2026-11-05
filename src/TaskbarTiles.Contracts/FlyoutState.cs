namespace TaskbarTiles.Contracts;

/// <summary>
/// A popup line; with <paramref name="Action"/> it is clickable and sends that action id to the connector.
/// <paramref name="Check"/>: draw a checkbox (ticked when true, the text dim and struck through) instead of the icon dot.
/// <paramref name="Indent"/>: nesting level, 0 to <see cref="FlyoutRow.MaxIndent"/>; each level moves the row right.
/// </summary>
public sealed record FlyoutRow(string Text, string? Value = null, TileIcon? Icon = null, string? Action = null, bool? Check = null,
    int Indent = 0)
{
    /// <summary>The deepest nesting a popup draws; deeper levels are drawn at this one.</summary>
    public const int MaxIndent = 4;
}

public sealed record FlyoutAction(string Id, string Label, bool Primary = false);

/// <summary>
/// A one-line text box in the popup; Enter sends the typed text to <see cref="ITileConnector.OnInputAsync"/> with
/// <paramref name="Action"/>. <paramref name="Secret"/>: a password box that shows dots, for keys and tokens.
/// </summary>
public sealed record FlyoutInput(string Action, string? Placeholder, bool Secret = false);

/// <summary>
/// A tile's popup. <paramref name="StayOpen"/>: a row, button or input action leaves the popup open and the host asks
/// for the popup again when the action is done, instead of closing it.
/// </summary>
public sealed record FlyoutState(
    string Title,
    string? Subtitle,
    IReadOnlyList<FlyoutRow> Rows,
    IReadOnlyList<FlyoutAction> Actions,
    byte[]? Image = null,
    string? Tint = null, // "#RRGGBB" the popup is tinted with; null keeps the plain backdrop
    FlyoutInput? Input = null,
    bool StayOpen = false);
