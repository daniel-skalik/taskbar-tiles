# Tiles on the right side of the taskbar — design

Date: 2026-10-29. Builds on the Milestone 3 layout (displays, slots, edit mode).

## Goal

With centered taskbar icons, tiles sit left of Start today. A display can instead put its tiles in the free space between the app icons and the tray, against the tray.

## Decisions (user)

- One side per display, not two slot lists and not overflow: `"side": "left"` (default, today's behaviour) or `"right"`.
- On the right, the bar is anchored against the tray; opening or closing apps changes only the free room on the icon side. The existing "when space runs out" rules (compact, then `»`) apply unchanged.
- Where the icons end is found in two phases: phase 1 from the taskbar's own window (`ReBarWindow32` child of the taskbar), phase 2 from UI Automation. The user checks phase 1 on screen first.

## Probe (user's PC, primary 3840 px wide at 150 %, physical pixels)

| Area | Left | Right |
|---|---|---|
| Tiles today (left zone) | 12 | 414 |
| Start + app icons (UI Automation) | 1589 | 2251 |
| `ReBarWindow32` / `MSTaskSwWClass` window | 1656 | 2118 |
| Tray (`TrayNotifyWnd`) | 3408 | 3840 |

The icon window ends 133 px before the last icon, so phase 1 can overlap the last icons when the bar grows that far left (here: more than ~1290 px of tiles). A targeted UI Automation read of `Taskbar.TaskListButtonAutomationPeer` bounding rectangles takes about 8–10 ms from PowerShell (35 ms the first time) and returns 2251.

## Config

```jsonc
"displays": { "primary": { "side": "right", "slots": [ "spotify", "claude" ] } }
```

- `"side"`: `"left"` or `"right"`, case-insensitive; anything else is a config error: `"side" on display 'primary' must be "left" or "right".`
- `DisplayLayout` gains `Side` (`BarSide.Left` by default). Saving a layout (edit mode, menu commands) writes `"side": "right"` and removes the key for left, keeping other keys.
- With left-aligned taskbar icons both sides give the same zone (icons to tray), as today.

## Zone

- `TaskbarInfo` gains `RightZone`: from the icons' right edge + margin to the tray's left edge − margin; on secondary taskbars (no tray window) to the taskbar edge − the clock reserve. With left-aligned icons it equals today's zone.
- Phase 1: the icons' right edge is the `ReBarWindow32` (secondary: `WorkerW`) window's right edge, falling back to Start's right edge, then the taskbar's left edge.
- Phase 2 (built): an `IIconEdgeReader` reads the last `Taskbar.TaskListButtonAutomationPeer`'s right edge with UI Automation on a background thread, only while a display uses the right side and only after the tracker's coalesced taskbar change events (no polling). When it fails, phase 1's edge plus one icon width (44 DIP) is used and the failure is logged once.
- `TileBar` uses `RightZone` and anchors right when its side is right; otherwise today's zone and anchoring.

## Menu

The bar's and tiles' right-click menus get **Move tiles to the right side** or **Move tiles to the left side** (whichever is not current), just above **Show config file**. It writes `"side"` for the display through the same edit path as other layout commands (refused like them when the display is shared through `"*"`).

## Testing

- `FreeZoneCalculator`: right zone for centered and left-aligned icons, secondary taskbar, missing icon window.
- `ConfigParser`: `side` left/right/missing/invalid; `ConfigJson.SetDisplay` writes and removes it.
- `TileMenu`: the move entry and its command for each side.
- `HostController`: a display's side reaches its bar; the move command saves the side.
- Phase 2: the edge reader behind an interface with a fake; the tracker uses its edge when present and the fallback when it fails.
