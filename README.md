# Timberborn Profiler

An in-game **CPU profiler overlay** for Timberborn modders. Press **Alt+Shift+P**
while a settlement is loaded, click **Start**, and it scans every loaded *mod*
assembly, Harmony-patches their tick/update methods, and live-displays CPU cost
**grouped per assembly and per class** — avg / p99 / max ms per active frame.

It answers "which mod (and which class in it) is eating my frame budget?" with
data instead of guesswork. Extracted from the TimberDevKit profiling harness
into a standalone, redistributable mod.

## Using it

| Control | Effect |
|---|---|
| **Alt+Shift+P** | Show / hide the window (drag it by the header) |
| **Start / Stop** | Install timing patches and begin / freeze sampling |
| **Vanilla: off/on** | Also profile vanilla `Timberborn.*` code (heavier — see below) |
| **Clear** | Re-baseline the stats (wipe cold-start spikes) |
| **Copy** | Copy the table to the clipboard |

Each row is one assembly, rolled up, with its classes indented beneath it.
Columns are **ms per active frame**: a sample is one unit's summed cost for one
frame, so an assembly's number is the true sum of its classes', and p99 means
"1% of active frames this unit cost more than X ms" — the figure that maps to a
frame-rate hitch.

## What it measures

The full tick + update family — `ITickableSingleton`, `IUpdatableSingleton`,
`ILateUpdatableSingleton`, `TickableComponent` (base class), `IUpdatableComponent`,
`ILateUpdatableComponent` — resolved by name at runtime, so the mod compiles and
loads whether or not any given profiled mod is installed.

"Mod code" = a method whose declaring type lives in a non-engine assembly.
Engine / BCL / DI / Harmony / this mod are *always* excluded; vanilla
`Timberborn.*` is excluded unless the **Vanilla** toggle is on.

**Blind spots** (inherent to a managed micro-profiler): work driven by Unity's
native MonoBehaviour magic methods (`Update`/`LateUpdate`/`FixedUpdate` on raw
GameObjects that bypass Timberborn's dispatch) isn't hooked, and pure engine
costs (ParticleSystem, Animator, rendering, physics) have no managed method to
patch. Use Unity's own profiler for those.

## Caveats

- **Overhead is opt-in.** Timing every component `Update`/`Tick` across all mods
  has real per-call cost, which is why it only starts when you click Start. The
  status line shows how many methods are patched so you know the surface size.
- **Vanilla mode is heavy.** It patches hundreds of vanilla types firing across
  thousands of instances per tick — informative, but it adds overhead and can
  inflate the numbers. Use it for the big picture, not precise mod measurement.
- **LateUpdate flush race.** `FrameDriver.LateUpdate` ordering vs. game
  components' `LateUpdate` is undefined, so a few late-update calls can roll into
  the next frame's sample.

## Build & deploy

```pwsh
# copy and edit the install-path template once
copy Directory.Build.local.props.example Directory.Build.local.props

dotnet build TimberbornProfiler.slnx                         # builds + deploys to Mods\TimberbornProfiler
dotnet build TimberbornProfiler.slnx -p:ProfilerDeploy=false # build only
```

Always builds Release (forced via `Directory.Build.props`). `TimberbornInstallDir`
resolves from (highest first): `-p:` on the CLI → `PROFILER_TIMBERBORN_DIR` /
`DEVKIT_TIMBERBORN_DIR` / `KEYSTONE_TIMBERBORN_DIR` env var →
`Directory.Build.local.props`. Deploy target defaults to
`%USERPROFILE%\Documents\Timberborn\Mods\TimberbornProfiler`; override with
`PROFILER_DEPLOY_DIR`. Requires the **Harmony** mod installed (manifest dependency).

## Config

`config.json` (deployed beside the DLL; the build won't overwrite an existing one):

```json
{ "ShowProfilerWindow": true, "LogToPlayerLog": true }
```

- `ShowProfilerWindow: false` → the mod loads but registers no window.
- `LogToPlayerLog: false` → suppress the start/stop info lines (warnings/errors still emit).

## Layout

```
TimberbornProfiler.slnx
├── manifest.json                mod manifest (Id SylvanGames.TimberbornProfiler; needs Harmony)
├── config.json                  runtime toggles
└── src/Profiler.Mod/
    ├── ProfilerModStarter.cs     IModStarter — resolves paths, applies logging toggle
    ├── ProfilerConfigurator.cs   [Context("Game")] — binds the window
    ├── ProfilerWindow.cs         the overlay (UI, keybind, buttons, render)
    ├── AssemblyProfilerSession.cs on-demand patch + per-frame accumulation engine
    ├── AssemblyScanner.cs         cross-assembly discovery + mod-code filter
    ├── RingStats.cs              ring buffer → avg/p99/max
    ├── FrameDriver.cs            per-frame LateUpdate flush heartbeat
    ├── ProfilerConfig.cs / ProfilerPaths.cs / ProfilerLog.cs
```
