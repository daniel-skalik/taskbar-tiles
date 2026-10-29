# Tiles on the right side of the taskbar — design

Date: 2026-10-29. Builds on the Milestone 3 layout (displays, slots, edit mode).

## Goal

With centered taskbar icons, tiles sit left of Start today. A display can also put tiles in the free space between the app icons and the tray, against the tray: each display has a left and a right side, used together.

## Decisions (user)

- Two slot lists per display, `"left"` and `"right"`, used together (first built as one `"side"` per display; after trying it the user asked for both at once). `"slots"` is read as `"left"`.
- Each side is its own bar window. The right bar is anchored against the tray; opening or closing apps changes only the free room on the icon side. The "when space runs out" rules (compact, then `»`) apply per side.
- With left-aligned icons both sides share the icons-to-tray space: the right bar takes its place against the tray first, the left bar ends a gap before it.
- Edit mode: both bars of every display edit at once; tiles drag between sides like between displays; each bar has its own `+` (adds to that side).
- Opening Start lifts the taskbar into a z-band above every topmost window, hiding the bars. Each bar is owned by its display's taskbar (`GWLP_HWNDPARENT`), so it moves with it.
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
"displays": { "primary": { "left": [ "spotify" ], "right": [ "claude" ] } }
```

- `"left"` and `"right"` are slot lists like `"slots"` was; either may be missing (empty). `"slots"` is the old name of `"left"`; both in one display is a config error: `Display 'primary' has both "slots" and "left"; "slots" is the old name of "left", keep one.`
- `HostConfig.Displays` holds a `DisplayEntry(Left, Right)` of two `DisplayLayout`s. Layout edits (`LayoutEdits`) work on one side's `DisplayLayout`.
- Saving an entry writes `"left"` (replacing `"slots"` in its place) and `"right"` only when it has slots (removed otherwise), keeping other keys.

## Zone

- `TaskbarInfo` gains `RightZone`: from the icons' right edge + margin to the tray's left edge − margin; on secondary taskbars (no tray window) to the taskbar edge − the clock reserve. With left-aligned icons it equals today's zone.
- Phase 1: the icons' right edge is the `ReBarWindow32` (secondary: `WorkerW`) window's right edge, falling back to Start's right edge, then the taskbar's left edge.
- Phase 2 (built): an `IIconEdgeReader` reads the last `Taskbar.TaskListButtonAutomationPeer`'s right edge with UI Automation on a background thread, only while a display uses the right side and only after the tracker's coalesced taskbar change events (no polling). When it fails, phase 1's edge plus one icon width (44 DIP) is used and the failure is logged once.
- Each `TileBar` has a fixed `Side`: the left bar uses today's zone and anchoring, the right bar `RightZone`, anchored right. With left-aligned icons the left bar's zone ends a gap before the right bar's left edge; the right bar reflows the left one when that edge moves.

## Menu and edits

- A tile's right-click menu gets **Move to the right side** or **Move to the left side** (the other side), above **Remove tile**. It moves the slot to the end of the other side in one save.
- `ILayoutEditor` methods take the bar's side (`EditLayout`, `RemoveTile`, `AddFromGallery`); `MoveToDisplay` takes both sides. A move onto a display skips tiles that display already places on either side. The `+` gallery lists tiles neither side of the display places.

## Testing

- `FreeZoneCalculator`: right zone for centered and left-aligned icons, secondary taskbar, missing icon window.
- `ConfigParser`: `left`/`slots`/`right`, missing sides, `slots` with `left`; `ConfigJson.SetDisplay` writes `right` only with slots and renames `slots` in place.
- `TileMenu`: the tile's move entry for each side; the bar menu has none.
- `HostController`: each side gets its own bar; moves between sides and onto another display's side; edits and removals touch only their side; icon edges are tracked only while a display has right-side tiles.
- Phase 2: the edge reader behind an interface with a fake; the tracker uses its edge when present and the fallback when it fails.
