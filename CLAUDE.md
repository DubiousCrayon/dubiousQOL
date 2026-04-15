# dubiousQOL — Claude session primer

Slay the Spire 2 mod: a collection of opt-in QOL patches. One assembly, Harmony-patched. Read this end-to-end before starting a new feature.

## Repo layout

```
MainFile.cs              ModInitializer. Loads config, runs Harmony.PatchAll().
DubiousConfig.cs         Per-feature on/off flags, JSON-persisted to user_data/mod_configs/dubiousQOL.cfg.
dubiousQOL.csproj        net9.0, refs sts2.dll (publicized) + 0Harmony.dll. Post-build copies .dll into the game's mods/ folder.
mod_manifest.json        has_pck=true, has_dll=true.
Patches/                 One file per feature. File name = feature name.
dubiousQOL/fonts/        Embedded fonts loaded via res://dubiousQOL/fonts/...
dubiousQOL/images/       Embedded sprites.
.tmp/                    Gitignored. Decompiled third-party mods for reference (see reference_decompiled_mods memory).
```

Every feature lives in `Patches/<Name>.cs`, early-returns on its `DubiousConfig.<Name>` flag, and is otherwise self-contained. Adding a feature = new file + new flag + `harmony.PatchAll()` picks it up automatically.

## Build + deploy

The only tool you need is `mcp__sts2-modding__build_mod` with `project_dir="C:\Users\aaa7v\dev\mods\sts2\dubiousQOL"`. It compiles and auto-copies the `.dll` into `C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\mods\`.

**File-lock caveat:** if the game is running, the copy step fails with MSB3027 ("file is used by SlayTheSpire2.exe") after 10 retries. The compile still succeeded — the user just needs to close the game, or use hot-reload. Do not treat this as a code error.

**Hot-reload:** `mcp__sts2-modding__bridge_hot_reload` with `tier=1` (patches only) or `tier=2` (+ entities) if the game is running with the MCPTest bridge loaded. `hot_reload_project` has a broken async import, avoid it.

## Game source lookups

`mcp__sts2-modding__get_entity_source` with a class name returns the full decompiled C# — use this before guessing APIs. Works for any game class (NButton, NMapScreen, SfxCmd, RunManager, etc.). Prefer this over grepping the decompiled mod dumps in `.tmp/` for game engine internals — those dumps are for studying other mods' *patterns*, not for game source.

Other useful MCP tools:
- `mcp__sts2-modding__search_game_code` — regex across all game source.
- `mcp__sts2-modding__list_hooks` / `suggest_hooks` — find Harmony targets.
- `mcp__sts2-modding__bridge_*` — live game inspection/control while the MCPTest mod is running (inspect nodes, take screenshots, start runs, etc.).

## Conventions

- **Nullable-enable** is on project-wide. Respect it.
- **Reflection over publicized access:** the project publicizes `sts2.dll` so private fields are visible, but many accesses still go through `Traverse.Create(...).Property("X").GetValue()` because publicization breaks on some generic properties. Either works; match what neighboring code does.
- **Logger:** `MainFile.Logger.Warn(...)`. Wrap patch bodies in try/catch and log — never let a mod exception crash a Harmony hook.
- **No comments that narrate** — only write a comment if the *why* is non-obvious (a timing constraint, a reflection workaround, a game-engine quirk the reader would otherwise trip on).
- **Commit style:** short imperative subject, one paragraph body explaining the *what* and notable constraints. No Co-Authored-By (see feedback_no_coauthor memory).

## Patterns that recur

**Adding a feature toggle:**
1. Add `public static bool FooBar = true;` in `DubiousConfig.cs`.
2. Add `if (dict.TryGetValue(nameof(FooBar), out var x)) FooBar = x;` in `Load()`.
3. Add the same key in the `Save()` dict.
4. In the patch: `if (!DubiousConfig.FooBar) return;` at the top of the postfix/prefix.
5. ModConfigUI.cs auto-picks up the flag if it's added to its known-features list — check that file when adding.

**UI reuse via scene cloning:** the cleanest way to get a styled Godot control is to instantiate the game's own `.tscn`, pluck the node you want, reparent it, then `QueueFree()` the rest. See `ActNameLabel.CreateBlank()` for the canonical template. For buttons that need game styling (back button, arrows), `Node.Duplicate(4)` (flags=4 = DUPLICATE_SCRIPTS only, skips the source's signal bindings) is preferred over subclassing.

**Modals:** `NModalContainer.Instance.Add(control, showBackstop: false)` opens a modal. The default `showBackstop: true` renders a 0.85-alpha black ColorRect underneath — opt out if you want the screen behind to show through.

**Hover/click SFX:** NButton subclasses get `event:/sfx/ui/clicks/ui_hover` on focus and `event:/sfx/ui/clicks/ui_click` on mouse-*down* for free. Raw Godot `TextureButton`/`Button` do NOT — wire them by hand:
```csharp
btn.MouseEntered += () => SfxCmd.Play("event:/sfx/ui/clicks/ui_hover");
btn.ButtonDown   += () => SfxCmd.Play("event:/sfx/ui/clicks/ui_click"); // NOT Pressed — that's release-time
```

**Heavy click actions:** if the Pressed handler does expensive synchronous work (instantiating scenes, NMapScreen takes ~50ms), defer it via `Callable.From(() => { ... }).CallDeferred()` so the audio event flushes in the current frame.

**Scene _Ready timing:** a cloned/duplicated node's `_Ready` does not fire until it's in the scene tree. If you call `.Enable()` or similar on a NButton clone before it's added, protected fields like `_outline` are null. `AddChild(clone)` first, then `Enable()`.

## Feature map (current)

| File | What it does |
|------|--------------|
| `ActNameDisplay.cs` | Per-act styled label next to the top-bar boss icon. Also exposes `ActNameLabel` helpers used by other features. |
| `DeckSearch.cs` | Text search on the mid-run deck view. |
| `IncomingDamageDisplay.cs` | Aggregated incoming damage number next to the HP bar. |
| `MapHistory.cs` | Captures per-act ActMap + drawings + visited coords, writes `{StartTime}.maps.json` sidecar outside the `history/` dir (game's loader rejects foreign files there). |
| `MapHistoryButton.cs` | Map-icon button on the run history screen; opens the viewer modal. |
| `MapHistoryScreenGuard.cs` | Harmony prefixes that skip singletons-only paths in NMapScreen so it can be instantiated outside a run. |
| `MapHistoryViewer.cs` | The modal itself. Reuses the real `NMapScreen` for pixel-accurate replay; side arrows cycle acts; NBackButton cloned; MapLegend hidden (overlaps right arrow). |
| `ModConfigUI.cs` | In-game toggle UI under settings. Must be updated when adding a feature flag. |
| `RarityDisplay.cs` | Shows rarity labels on card rewards. |
| `SkipSplash.cs` | Skips the MegaCrit intro video. |
| `UnifiedSavePath.cs` | Forces modded and unmodded saves to share the same folder. |
| `WinStreakDisplay.cs` | Win-streak flame badge on the top bar. |

## Gotchas collected from prior sessions

- **History sidecars MUST live outside `history/`** — `LoadAllRunHistoryNames` enumerates everything in that dir and anything that isn't `.corrupt`/`.backup` gets fed to `LoadRunHistory`, fails, and is renamed `.corrupt` by migration. Use a sibling `map_history/` directory.
- **RunHistorySaveManager.GetHistoryPath returns a relative-ish path** (`{profileDir}/saves/history`, no platform/userId prefix). Don't prepend `user://` to it. Use `UserDataPathProvider.GetProfileScopedPath(profileId, UserDataPathProvider.SavesDir)` and `ProjectSettings.GlobalizePath`.
- **NMapScreen capture timing:** the outgoing act's map is still on `State.Map` during `RunManager.EnterAct`'s prefix — `GenerateMap` later in the same frame overwrites it. Snapshot in the prefix, not after.
- **Final-act map** is only in `SerializableRun.Acts[current].SavedMap`, populated when the run ends. Merge in the `RunHistoryUtilities.CreateRunHistoryEntry` postfix.
- **Drawings align to a -600 Y resting position.** StartOfActAnim lerps to that during a real run; when bypassing the anim, set `_mapContainer.Position = (0, -600)` and `_targetDragPos = (0, -600)` or strokes land 600px off.
- **NMapBg paint** requires calling `OnVisibilityChanged` (via reflection) after `Initialize(runState)` — visibility-changed is the only path that actually assigns textures, and it only fires on visibility flips.

## Memory

The user's auto-memory (`C:\Users\aaa7v\.claude\projects\C--Users-aaa7v-dev-mods-sts2-dubiousQOL\memory\`) holds durable context about dev environment, preferences, and decompiled mod source locations. `MEMORY.md` there is the index — always current conversation context. Append new feedback/reference/project memories there rather than re-deriving them.
