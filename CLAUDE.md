# dubiousQOL — Claude session primer

Slay the Spire 2 mod: a collection of opt-in QOL patches. One assembly, Harmony-patched. Read this end-to-end before starting a new feature.

## Repo layout

```
MainFile.cs              ModInitializer. Loads config, runs Harmony.PatchAll().
DubiousConfig.cs         Per-feature on/off flags, JSON-persisted to user_data/mod_configs/dubiousQOL.cfg.
dubiousQOL.csproj        net9.0, refs sts2.dll (publicized) + 0Harmony.dll. Post-build copies .dll into the game's mods/ folder.
mod_manifest.json        has_pck=true, has_dll=true.
Patches/                 One subdirectory per feature. Harmony patches + feature-specific logic.
UI/                      Shared UI building blocks (see "Shared libraries" below).
UI/Custom/               Custom-built widgets from raw Godot controls (not game-asset clones).
Utilities/               Shared non-UI utilities (reflection, I/O, combat math, node helpers).
dubiousQOL/fonts/        Embedded fonts loaded via res://dubiousQOL/fonts/...
dubiousQOL/images/       Embedded sprites.
.tmp/                    Gitignored. Decompiled third-party mods for reference (see reference_decompiled_mods memory).
```

Every feature lives in `Patches/<Name>/`, early-returns on its config flag, and uses shared `UI/`/`Utilities/` for anything reusable. Adding a feature = new subdirectory + new config + `harmony.PatchAll()` picks it up automatically.

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

## Shared libraries — USE THEM FIRST

**This is a hard rule:** before writing any UI construction, node manipulation, styling, reflection, I/O, or combat logic in a feature file, check `UI/` and `Utilities/` for an existing helper. If one exists, use it. If one doesn't exist but the functionality is generic enough that another mod or feature could use it, **create it in the shared library first**, then call it from the feature.

Feature files in `Patches/` should contain only Harmony patches and feature-specific orchestration. Anything reusable belongs in `UI/` or `Utilities/`.

### UI/ — game-asset cloning and shared UI construction

| File | What it provides |
|------|------------------|
| `ButtonHelper.cs` | Hover/click SFX wiring, anchor positioning, toggle state, game arrow cloning (`CloneGameArrow` + `ResetClonedArrow`) |
| `CloneHelper.cs` | Named `Duplicate()` flag constants (`VisualOnly`, `ScriptsOnly`, `Full`, etc.) + typed `Clone<T>()` / `CloneWithMaterial<T>()` |
| `FontHelper.cs` | Font loading by string identifier (`"fightkid"`, `"kreon-bold"`, etc.), caching, `GetPath()` for BBCode |
| `ModalHelper.cs` | Back buttons (scene preload or clone), error panels, escape handling, confirmation popups |
| `StyleHelper.cs` | `MakeStyleBox`, `CreateDarkPanel`, `CreateDivider`, `CreateSectionHeader`, `CreateSubSectionHeader`, `CreateDimLabel` |
| `TabHelper.cs` | Tab acquisition from game scenes, cloning, `CreateTabBar()`, `WireTabSwitching()` |
| `Theme.cs` | Shared design tokens — panel colors, text colors, stat colors, hover values |
| `WidgetHelper.cs` | Game-cloned settings widgets: tickbox, slider, button, info label, settings row |
| `ActNameLabel.cs` | Per-act styled MegaLabel (scene extraction from act_banner), `ApplyStyle()`, `GetMargins()` |
| `RarityHelper.cs` | `RarityColors` (relic/potion color maps), `RarityIconGenerator` (procedural diamond icons), `CompendiumHeaderRecolor` (BBCode tag swapping) |
| `SpriteFrameLoader.cs` | Numbered PNG frame loading with caching, `FrameIndexAt()` for animation timing |
| `SourceIconResolver.cs` | Entity name → icon texture + semantic color resolution (cards, relics, potions, monsters, orbs, debuffs) |

### UI/Custom/ — original widgets (not game clones)

| File | What it provides |
|------|------------------|
| `Widgets.cs` | `StyleButton`, `StyleTabButton`, `CreateArrowButton`, `CreateToggleButton`, `CreateStyledRichLabel` (overlay text), `CreateArchLabel` (circular arc text) |

### Utilities/ — non-UI shared logic

| File | What it provides |
|------|------------------|
| `NodeHelper.cs` | `FindDescendant<T>()`, `ExtractFromScene<T>()` (scene plucking), `ExtractTextureFromScene()` (texture reads from scenes) |
| `ReflectionHelper.cs` | `SetField`, `GetField`, `SetProperty` wrappers with BindingFlags |
| `SidecarIO.cs` | Profile-scoped path resolution, `WriteJson<T>`, `ReadJson<T>`, `TryDelete` |
| `CombatPredictor.cs` | `PredictIncoming()` (damage + HP loss), `PredictEndOfTurnBlockGain()`, `ReadPreviewDamage/HpLoss()`, `GetBeatingRemnantCap()` |

## Patterns that recur

**Adding a feature:**
1. Create `Patches/<Name>/` with the Harmony patch file(s).
2. Create a `<Name>Config.cs` subclass of `FeatureConfig` — set `Id`, `Name`, `Description`, `EnabledByDefault`. Override `DefineEntries(EntryBuilder b)` for any sub-settings.
3. In the patch: `if (!<Name>Config.Instance.Enabled) return;` at the top of the postfix/prefix.
4. `ConfigRegistry` auto-discovers all `FeatureConfig` subclasses via assembly reflection. `ModConfigUI` iterates `ConfigRegistry.All` — no manual registration needed.
5. Update the feature map table in this file.

**Removing a feature:**
1. Delete the entire `Patches/<Name>/` directory (patch files, config class, `.uid` files).
2. Remove the feature's row from the feature map table in this file.
3. That's it — `ConfigRegistry` discovers via reflection, so removing the class removes it from ModConfigUI automatically. The user's stale `{Id}.json` config file on disk is harmless and ignored.

**Scene plucking:** use `NodeHelper.ExtractFromScene<T>(scenePath, nodePath)` to extract a node from a game scene, or `NodeHelper.ExtractTextureFromScene(scenePath, candidatePaths)` to read a texture. See `ActNameLabel.CreateBlank()` for the canonical pattern. Never write inline instantiate→find→detach→QueueFree boilerplate in feature files.

**Node cloning:** use `CloneHelper.Clone<T>(source, CloneHelper.ScriptsOnly)` with named flag constants. For buttons/arrows, use `ButtonHelper.CloneGameArrow()` + `ButtonHelper.ResetClonedArrow()`. For tabs, use `TabHelper.CreateTab()`.

**Modals:** use `ModalHelper.CreateBackButton(parent, name)` for back buttons, `ModalHelper.ShowConfirmation()` for popups. `NModalContainer.Instance.Add(control, showBackstop: false)` opens a modal.

**Hover/click SFX:** use `ButtonHelper.WireHoverAndClickSfx(btn, btnSize)` for raw Godot buttons. NButton subclasses get this for free.

**Styling:** use `StyleHelper.MakeStyleBox()`, `StyleHelper.CreateDarkPanel()`, `StyleHelper.CreateDivider()`. Use `Theme.*` for color tokens. For custom-built toggle/arrow buttons, use `Widgets.*` from `UI/Custom/`.

**Heavy click actions:** if the Pressed handler does expensive synchronous work (instantiating scenes, NMapScreen takes ~50ms), defer it via `Callable.From(() => { ... }).CallDeferred()` so the audio event flushes in the current frame.

**Scene _Ready timing:** a cloned/duplicated node's `_Ready` does not fire until it's in the scene tree. If you call `.Enable()` or similar on a NButton clone before it's added, protected fields like `_outline` are null. `AddChild(clone)` first, then `Enable()`. For arrows, this means `CloneGameArrow()` before AddChild, `ResetClonedArrow()` after.

## Feature map (current)

Each feature lives in `Patches/<Name>/`. Shared helpers live in `UI/` and `Utilities/`.

| Feature | Files | What it does |
|---------|-------|--------------|
| `ActNameDisplay/` | patch + config | Per-act styled label next to the top-bar boss icon. Uses `UI/ActNameLabel.cs`. |
| `DeckSearch/` | patch + config | Text search on the mid-run deck view. Uses `NodeHelper.ExtractFromScene`. |
| `IncomingDamageDisplay/` | patch + config | Aggregated incoming damage/HP loss next to the HP bar. Uses `CombatPredictor`, `Widgets.CreateStyledRichLabel`. |
| `MapHistory/` | 4 files + config | Captures per-act maps, writes sidecar JSON. Button + modal viewer with game arrows. Uses `ButtonHelper`, `ModalHelper`, `NodeHelper`, `SidecarIO`. |
| `ModConfigUI/` | patch | In-game toggle UI under settings. Uses `CloneHelper`, `ModalHelper`, `TabHelper`, `WidgetHelper`, `StyleHelper`, `Theme`. |
| `RarityDisplay/` | patch + config | Rarity coloring on compendium headers and hover tips. Uses `UI/RarityHelper.cs`. |
| `StatsTracker/` | 5 files + config | Per-combat stats overlay + run history viewer. Uses `ButtonHelper`, `ModalHelper`, `TabHelper`, `StyleHelper`, `Theme`, `SidecarIO`, `Widgets`. |
| `UnifiedSavePath/` | patch + config | Forces modded and unmodded saves to share the same folder. |
| `WinStreakDisplay/` | patch + config | Win-streak flame badge on the top bar. Uses `FontHelper`, `SpriteFrameLoader`, `Widgets.CreateArchLabel`. |

## Gotchas collected from prior sessions

- **History sidecars MUST live outside `history/`** — `LoadAllRunHistoryNames` enumerates everything in that dir and anything that isn't `.corrupt`/`.backup` gets fed to `LoadRunHistory`, fails, and is renamed `.corrupt` by migration. Use a sibling `map_history/` directory.
- **RunHistorySaveManager.GetHistoryPath returns a relative-ish path** (`{profileDir}/saves/history`, no platform/userId prefix). Don't prepend `user://` to it. Use `UserDataPathProvider.GetProfileScopedPath(profileId, UserDataPathProvider.SavesDir)` and `ProjectSettings.GlobalizePath`.
- **NMapScreen capture timing:** the outgoing act's map is still on `State.Map` during `RunManager.EnterAct`'s prefix — `GenerateMap` later in the same frame overwrites it. Snapshot in the prefix, not after.
- **Final-act map** is only in `SerializableRun.Acts[current].SavedMap`, populated when the run ends. Merge in the `RunHistoryUtilities.CreateRunHistoryEntry` postfix.
- **Drawings align to a -600 Y resting position.** StartOfActAnim lerps to that during a real run; when bypassing the anim, set `_mapContainer.Position = (0, -600)` and `_targetDragPos = (0, -600)` or strokes land 600px off.
- **NMapBg paint** requires calling `OnVisibilityChanged` (via reflection) after `Initialize(runState)` — visibility-changed is the only path that actually assigns textures, and it only fires on visibility flips.

## Memory

The user's auto-memory (`C:\Users\aaa7v\.claude\projects\C--Users-aaa7v-dev-mods-sts2-dubiousQOL\memory\`) holds durable context about dev environment, preferences, and decompiled mod source locations. `MEMORY.md` there is the index — always current conversation context. Append new feedback/reference/project memories there rather than re-deriving them.
