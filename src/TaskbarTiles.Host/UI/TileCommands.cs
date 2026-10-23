using System.IO;
using TaskbarTiles.Contracts;
using TaskbarTiles.Host.Runtime;

namespace TaskbarTiles.Host.UI;

/// <summary>Commands on one tile, from its context menu or its error popup.</summary>
public enum TileCommand { Configure, Refresh, ShowLog, Restart }

/// <summary>Commands on the whole app.</summary>
public enum BarCommand { EditLayout, DoneEditing, OpenConfig, Exit }

/// <summary>Commands that change the layout (saved to config.json) or a stack's rotation (not saved).</summary>
public enum LayoutCommand { MoveLeft, MoveRight, AddToStack, RemoveFromStack, PauseRotation, ResumeRotation, RemoveTile }

/// <summary>One context-menu line; an entry with no command is a separator.</summary>
public sealed record MenuEntry(string Header, TileCommand? Tile = null, BarCommand? Bar = null, LayoutCommand? Layout = null,
    bool Enabled = true)
{
    public static MenuEntry Separator { get; } = new("");
    public bool IsSeparator => Tile is null && Bar is null && Layout is null;
}

/// <summary>What the menu needs to know about the right-clicked slot.</summary>
public sealed record SlotMenuState(bool IsFirst, bool IsLast, bool IsStack, bool RotationPaused, bool CanStack);

public static class TileMenu
{
    /// <summary>The right-click menu of a slot, or of the bar's gaps when <paramref name="slot"/> is null.</summary>
    public static IReadOnlyList<MenuEntry> Entries(SlotMenuState? slot, bool editing)
    {
        var editEntry = editing ? new MenuEntry("Done editing", Bar: BarCommand.DoneEditing) : new MenuEntry("Edit layout", Bar: BarCommand.EditLayout);
        var entries = new List<MenuEntry>();
        if (slot is not null)
        {
            entries.Add(new("Configure connector…", TileCommand.Configure));
            entries.Add(new("Refresh now", TileCommand.Refresh));
            entries.Add(new("Show connector log", TileCommand.ShowLog));
            entries.Add(new("Restart tile", TileCommand.Restart));
            entries.Add(MenuEntry.Separator);
            entries.Add(new("Move left", Layout: LayoutCommand.MoveLeft, Enabled: !slot.IsFirst));
            entries.Add(new("Move right", Layout: LayoutCommand.MoveRight, Enabled: !slot.IsLast));
            if (slot.IsStack)
            {
                entries.Add(new("Remove from stack", Layout: LayoutCommand.RemoveFromStack));
                entries.Add(slot.RotationPaused
                    ? new("Resume rotation", Layout: LayoutCommand.ResumeRotation)
                    : new("Pause rotation", Layout: LayoutCommand.PauseRotation));
            }
            else
            {
                entries.Add(new("Add to stack", Layout: LayoutCommand.AddToStack, Enabled: slot.CanStack));
            }
            entries.Add(MenuEntry.Separator);
            entries.Add(editEntry);
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
}

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
                case TileCommand.Configure:
                    Open(configFile);
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
