using System.IO;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Config;
using TaskbarTiles.Host.Runtime;

namespace TaskbarTiles.Host.UI;

/// <summary>Commands on one tile, from its context menu or its error popup.</summary>
public enum TileCommand { Settings, ShowFolder, Refresh, ShowLog, Restart }

/// <summary>Commands on the whole app.</summary>
public enum BarCommand { EditLayout, DoneEditing, OpenConfig, Exit }

/// <summary>Commands that change the layout (saved to config.json) or a stack's rotation (not saved).</summary>
public enum LayoutCommand { CreateStack, AddToStack, RemoveFromStack, PauseRotation, ResumeRotation, MoveToOtherSide, RemoveTile }

/// <summary>
/// One context-menu line; an entry without a command is a separator.
/// </summary>
public sealed record MenuEntry(string Header, TileCommand? Tile = null, BarCommand? Bar = null, LayoutCommand? Layout = null,
    bool Enabled = true)
{
    public static MenuEntry Separator { get; } = new("");
    public bool IsSeparator => Tile is null && Bar is null && Layout is null;
}

/// <summary>A slot of this display as the menu sees it: config index, tile ids and the title its first member shows.</summary>
public sealed record MenuSlot(int Index, bool IsStack, IReadOnlyList<string> TileIds, string? Title);

/// <summary>A plain tile a stack picker offers: its slot's config index, its id and the label shown.</summary>
public sealed record StackChoice(int SlotIndex, string TileId, string Label);

/// <summary>What the menu needs to know about the right-clicked slot; HasSettings: the shown tile's kind declares fields.</summary>
public sealed record SlotMenuState(bool IsStack, bool RotationPaused, bool HasSettings = false);

public static class TileMenu
{
    /// <summary>
    /// The right-click menu of a slot, or of the bar's gaps when <paramref name="slot"/> is null. <paramref name="side"/>
    /// is the bar the slot is on; its menu offers to move the slot to the other side.
    /// </summary>
    public static IReadOnlyList<MenuEntry> Entries(SlotMenuState? slot, bool editing, BarSide side = BarSide.Left)
    {
        var editEntry = editing ? new MenuEntry("Done editing", Bar: BarCommand.DoneEditing) : new MenuEntry("Edit layout", Bar: BarCommand.EditLayout);
        var entries = new List<MenuEntry>();
        if (slot is not null)
        {
            if (slot.HasSettings) entries.Add(new("Settings…", TileCommand.Settings));
            entries.Add(new("Show tile folder", TileCommand.ShowFolder));
            entries.Add(new("Refresh now", TileCommand.Refresh));
            entries.Add(new("Show connector log", TileCommand.ShowLog));
            entries.Add(new("Restart tile", TileCommand.Restart));
            entries.Add(MenuEntry.Separator);
            if (slot.IsStack)
            {
                entries.Add(new("Add to stack…", Layout: LayoutCommand.AddToStack)); // opens the stack picker panel
                entries.Add(new("Remove from stack", Layout: LayoutCommand.RemoveFromStack));
                entries.Add(slot.RotationPaused
                    ? new("Resume rotation", Layout: LayoutCommand.ResumeRotation)
                    : new("Pause rotation", Layout: LayoutCommand.PauseRotation));
            }
            else
            {
                entries.Add(new("Create stack…", Layout: LayoutCommand.CreateStack)); // opens the stack picker panel
            }
            entries.Add(MenuEntry.Separator);
            entries.Add(editEntry);
            entries.Add(side == BarSide.Right
                ? new MenuEntry("Move to the left side", Layout: LayoutCommand.MoveToOtherSide)
                : new MenuEntry("Move to the right side", Layout: LayoutCommand.MoveToOtherSide));
            entries.Add(new("Remove tile", Layout: LayoutCommand.RemoveTile));
            entries.Add(MenuEntry.Separator);
        }
        else
        {
            entries.Add(editEntry);
            entries.Add(MenuEntry.Separator);
        }
        entries.Add(new("Show config file", Bar: BarCommand.OpenConfig));
        entries.Add(MenuEntry.Separator);
        entries.Add(new("Exit Taskbar Tiles", Bar: BarCommand.Exit));
        return entries;
    }

    /// <summary>
    /// The plain tiles of this display a stack picker offers for the clicked slot: for a plain tile every other plain tile
    /// (never one with the clicked tile's id), for a stack every plain tile not in it yet. Each is labelled with the title
    /// its tile shows, or its id when that title has no letter or digit; labels that would repeat get the id appended.
    /// </summary>
    public static IReadOnlyList<StackChoice> StackChoices(IReadOnlyList<MenuSlot> slots, int clicked)
    {
        var target = slots.FirstOrDefault(s => s.Index == clicked);
        if (target is null) return [];
        var candidates = slots
            .Where(s => !s.IsStack && s.TileIds.Count == 1 && s.Index != clicked && !target.TileIds.Contains(s.TileIds[0]))
            .ToList();
        var labels = candidates.Select(Label).ToList();
        return candidates.Select((s, i) =>
        {
            var id = s.TileIds[0];
            var repeated = labels[i] != id && labels.Count(l => l == labels[i]) > 1;
            return new StackChoice(s.Index, id, repeated ? $"{labels[i]} ({id})" : labels[i]);
        }).ToList();
    }

    static string Label(MenuSlot slot) =>
        slot.Title is { } title && title.Any(char.IsLetterOrDigit) ? title.Trim() : slot.TileIds[0];}

/// <summary>Executes tile commands. Called on the UI thread; never throws.</summary>
public sealed class TileCommands(string configFile, string logsDir, Action<string> openFile, ITileLog log)
{
    /// <summary>Same file FileLog.For(logsDir, $"tile-{id}") writes.</summary>
    public string LogPathFor(string tileId) => Path.Combine(logsDir, FileLog.Sanitize($"tile-{tileId}") + ".log");

    public static bool TryFromActionId(string actionId, out TileCommand command)
    {
        switch (actionId)
        {
            case TileRuntime.RestartActionId: command = TileCommand.Restart; return true;
            case TileRuntime.ShowLogActionId: command = TileCommand.ShowLog; return true;
            default: command = default; return false;
        }
    }

    /// <summary>Popup and button actions: the host's own action ids become commands, everything else goes to the connector.</summary>
    public Task InvokeAsync(TileRuntime runtime, string actionId) =>
        TryFromActionId(actionId, out var command) ? ExecuteAsync(runtime, command) : runtime.InvokeActionAsync(actionId);

    public async Task ExecuteAsync(TileRuntime runtime, TileCommand command)
    {
        try
        {
            switch (command)
            {
                case TileCommand.ShowFolder:
                    // The kind folder holds tile.json and the scripts; config.json when the folder is gone.
                    Open(Directory.Exists(runtime.TileDirectory) ? runtime.TileDirectory : configFile);
                    break;
                case TileCommand.ShowLog:
                    Open(EnsureExists(LogPathFor(runtime.TileId)));
                    break;
                case TileCommand.Restart:
                    await runtime.RestartAsync();
                    break;
                case TileCommand.Refresh:
                    // A crashed connector cannot refresh; a restart is what the user wants.
                    if (runtime.LastError is { Fatal: true }) await runtime.RestartAsync();
                    else await runtime.InvokeActionAsync(TileActions.Refresh);
                    break;
            }
        }
        catch (Exception ex)
        {
            log.Error($"Command {command} on tile '{runtime.TileId}' failed", ex);
        }
    }

    public void OpenConfig() => Open(configFile);

    void Open(string path)
    {
        try { openFile(path); }
        catch (Exception ex) { log.Error($"Could not open {path}", ex); }
    }

    static string EnsureExists(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) File.WriteAllText(path, "");
        return path;
    }
}
