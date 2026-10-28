# Claude sessions tile — design

Date: 2026-10-28. Builds on [2026-10-27-tile-folders-design.md](2026-10-27-tile-folders-design.md).

## Goal

A tile that shows whether Claude Code sessions (Claude desktop app, terminal and background agents) are working, so the user knows when to switch back.

## Data source

`claude agents --json` (official CLI, "for scripting; does not require a TTY", ~0.3 s). It prints the active sessions as a JSON array and leaves out sessions whose process ended. Each entry has `name`, `cwd`, `kind` and either:

- `status`: `busy` or `idle` (interactive sessions), or
- `state`: e.g. `blocked` (background agents).

Rejected: reading `~/.claude/sessions/*.json` (undocumented, stale files stay behind); hooks writing status files (would show "waiting for you" but edit the user's `settings.json`). An interactive session waiting on a permission prompt shows as idle; that is what the CLI reports.

## Kind

Bundled script kind `tiles\claude\` (`tile.json`, `claude.ps1`), replaced on install like `uptime` and `ping`.

- `interval` every 5 s prints the tile:
  - title `Claude`; subtitle the counts, most urgent first, e.g. `1 blocked · 2 working · 4 idle` (zero counts left out; the tile cuts long text at the end);
  - icon `warning`/`warn` when any session is blocked, else `sync`/`accent` when any is busy, else `check`/`ok`;
  - no sessions: subtitle `No sessions`, icon `check`/`neutral`;
  - tooltip: names of working and blocked sessions.
- `flyout` prints the popup: title `Claude sessions`, one row per session (name, state shown as `working`/`idle`/`blocked`/the raw value, dot `accent`/`neutral`/`warn`/`neutral`), blocked then working then the rest; primary action `open`, label `Open Claude`.
- `action:open` brings the Claude desktop app to the front: the Start-menu app named `Claude` is launched through `shell:AppsFolder\<AppID>`, which focuses a running instance. Prints nothing.

## Finding claude

Setting `"claude"` (a path) if set, else `claude` on `PATH`, else `%USERPROFILE%\.local\bin\claude.exe`. Not found, non-zero exit or non-JSON output: the script writes the reason to stderr and exits 1, so the tile shows the existing error look.

## Testing

- `BundledKindsTests` lists `claude` among the bundled kinds; its hooks are valid.
- Integration tests run `claude.ps1` with `"claude"` pointing at a stub `.cmd` that prints a recorded sample, and check the tile, the popup, the empty case and the error case.
- `install.ps1` replaces `tiles\claude` with the other bundled kinds.
