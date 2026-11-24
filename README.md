<div align="center">
  <img src="assets/ampersand.png" width="256" alt="Ampersand logo" />
  <h1>Ampersand</h1>
  <p><b>The s&amp;box launcher for Linux.</b></p>
  <p>Client, editor and dedicated server, with Steam Runtime support, dependency checks and one-click engine builds.</p>
  <p>
    <img src="https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet&logoColor=white" alt=".NET 10" />
    <img src="https://img.shields.io/badge/Avalonia-12-8A2BE2?style=flat-square" alt="Avalonia 12" />
    <img src="https://img.shields.io/badge/Linux-x86__64-FCC624?style=flat-square&logo=linux&logoColor=black" alt="Linux x86_64" />
    <img src="https://img.shields.io/badge/AppImage-supported-7393C1?style=flat-square&logo=appimage&logoColor=white" alt="AppImage" />
  </p>
</div>

## What is this?

Ampersand is a small desktop launcher for running [s&amp;box](https://sbox.game)
from source on Linux. The engine ships as prebuilt natives plus managed code,
needs a handful of host libraries, and behaves best inside Valve's
**Steam Linux Runtime 4.0 (steamrt4)** container. Ampersand wraps all of this
behind a single window.

It can:

- **Launch** the game client (`sbox`), the editor (`sbox-dev`) and a
  **dedicated server** (`sbox-server`), each in its own terminal window,
  or headless in the background with logging.
- **Launch inside the Steam Runtime** container (per-target toggle) so the
  engine sees the exact libraries it was built against.
- **Check dependencies** on the host *and* inside the container, where the
  missing sets are different (notably `libunwind`, which the container may
  not ship. Ampersand keeps a compat cache for those).
- **Build s&amp;box** from source: run the engine's `Setup.sh`, then an
  `ldd` report over the natives, in a real terminal with colour.
- Remember your **s&amp;box checkout location** and your **server game**
  (package ident like `fss.bloodsigil` or a path to a `.sbproj`).

The UI is [Avalonia](https://avaloniaui.net/) with the Fluent dark theme.
There is deliberately no embedded terminal: every desktop already has a real
one with better scrollback, selection and colour handling, so Ampersand
spawns yours and gets out of the way.

## Requirements

| Requirement | Notes |
|---|---|
| Linux x86_64 | Primary target. Wayland works (Qt is forced to `xcb`/XWayland, SDL to the `x11` driver). |
| s&amp;box source checkout | A folder containing `game/` + `engine/` with `game/sbox` built. |
| .NET 10 SDK | Only to *build* Ampersand. Not needed to *run* the AppImage. |
| Steam + Steam Linux Runtime 4.0 (steamrt4) | Only for containerised launches / container dependency sweep. Install via `steam steam://install/4183110`. Steam must be running. |
| A terminal emulator | `gnome-terminal`, `konsole`, `alacritty`, `kitty`, `foot`, `xterm`, … Launches open there so you get a real TTY (and the engine's own ANSI colour). |

## Quick start

### Option A: AppImage (recommended)

```sh
./install-appimage.sh
./Ampersand-x86_64.AppImage
```

No `dotnet` needed at runtime: the default build is self-contained
(~37 MB download). It also installs itself: the AppImage is copied to
`~/Applications/`, a menu entry goes to
`~/.local/share/applications/Ampersand.desktop` and the icon to
`~/.local/share/icons/`.

### Option B: dev build

```sh
./bootstrap.sh                      # dotnet build -c Release
./bin/Release/net10.0/ampersand
```

## Building

### Dev build

`bootstrap.sh` is a thin wrapper over `dotnet build` (extra args pass through):

```sh
./bootstrap.sh                      # Release build into bin/Release/net10.0/
./bootstrap.sh -c Debug             # or Debug, etc.
```

Run it with `dotnet run` if you prefer:

```sh
dotnet run -c Release
```

### AppImage

`install-appimage.sh` publishes Ampersand and packs it with `appimagetool`, (download tool if
missing → prepare AppDir → populate `usr/` tree → repack → clean up):

```sh
./install-appimage.sh [OPTIONS]
```

| Option | Default | Description |
|---|---|---|
| `--source DIR` | script's dir | Ampersand source checkout |
| `--output FILE` | `<source>/Ampersand-x86_64.AppImage` | Where to write the AppImage |
| `--publish-dir DIR` | `<source>/publish/linux-x64` | `dotnet publish` staging dir |
| `--configuration NAME` | `Release` | `Release` or `Debug` |
| `--runtime RID` | `linux-x64` | .NET runtime identifier |
| `--self-contained` | **on** | Bundle the runtime (~93 MB staging → ~37 MB AppImage). Needs nothing on the host. |
| `--framework-dependent` | off | Small build (~23 MB staging) but requires the .NET 10 runtime installed. |
| `--icon FILE` | `<source>/assets/ampersand.png` | App icon PNG |
| `--base-appimage FILE` | (none) | Extract the AppDir skeleton (AppRun, libs, `runtime/`) from an existing AppImage instead of starting minimal; only `usr/bin`, the desktop entry and icon are replaced. |
| `--install-dir DIR` | `~/Applications` | Where to install the finished AppImage, with its `.desktop` entry (runs in a terminal) and icon. |
| `--no-install` | off | Skip the install step, leave the AppImage at `--output` only. |
| `--no-build` | off | Skip `dotnet publish`, just repackage the existing publish dir. |
| `-h`, `--help` | (none) | Show help. |

Examples:

```sh
# Standard self-contained AppImage
./install-appimage.sh

# Small build for a machine that already has the .NET 10 runtime
./install-appimage.sh --framework-dependent --output ~/Ampersand.AppImage

# Fast iteration on packaging (desktop file, icon, AppRun) without recompiling
./install-appimage.sh --no-build
```

To remove an installed AppImage again (installed file, menu entry, icon
and Development menu pinning):

```sh
./uninstall.sh
```

## Usage

### First run

If no valid s&amp;box location is known (persisted settings, then walking up
from the binary), Ampersand asks you to pick it. A valid location contains
`game/` and `engine/` with the `game/sbox` binary. You can change it later in
the **S&BOX LOCATION** field in the sidebar (Enter or click away to apply,
`…` to browse).

### Launch targets

| Row | Script | What it starts |
|---|---|---|
| Client (sbox) | `sbox.sh` | The game client |
| Editor (sbox-dev) | `sbox-dev.sh` | The editor (no `-project` opens the sbox launcher) |
| Dedicated Server (sbox-server) | `sbox-server.sh` | Headless server, needs `+game <ident\|/path/to.sbproj>` |

Point to your S&box repo (sbox-public), Select a row, 
set the toggles, hit play.
**Stop** kills a background run (for terminal runs it closes the window;
emulators that fork can't always take the engine with them). sbox-dev detaches
its launcher, so Stop closes its tab but leaves a running launcher alone.
Crashes will show a dialog to help you debug.

Per-target toggles:

- **Launch in Steam Runtime**: enter the steamrt4 container via Steam's own
  launcher service (Steam must be running).
- **Launch with system terminal**: open your emulator for this run (default
  on; it's the only way to see engine output). Off runs headless with output
  captured to the log file.

Global toggles (persisted in settings, take effect on next launch):

- **Casefold**: case-insensitive fallback for wrong-case asset references.
  On Linux the content step needs it: the native model compiler opens FBX and
  child-model `.meta` references at the case they were authored with, and the
  engine content has several that only resolve on a case-insensitive
  filesystem (without it citizen fails 4 assets and the run ends
  `Content: Failed`). **Build S&Box always preloads the shim** - both
  `ampersand --bootstrap` and `apps/build.sh` compile or reuse it - so this
  toggle only decides whether launches get it too; if the shim cannot be built
  the build says the content failure is not the checkout's. Textures are covered
  separately by the case aliases Build S&Box creates, which work either way.
  For a raw `sh Setup.sh` outside Ampersand, put the shim in the environment
  yourself: `LD_PRELOAD=~/.cache/sbox-ampersand/libcasefold.so sh Setup.sh`.
- **Input debug**: engine input diagnostics (`SBOX_INPUT_DEBUG`), so the flag
  no longer has to be in Ampersand's own environment at startup.

### Dedicated server game

When `sbox-server` is selected, a **DEDICATED SERVER GAME** field appears.
Enter a package ident (`fss.causal`) or browse for a local `.sbproj`;
Ampersand passes it as `+game <value>`.

### Tools (sidebar)

- **Build S&Box**: runs the engine's `Setup.sh` (git hooks, per-platform
  artifacts, bindings, managed, shaders, content), then Ampersand's own `ldd`
  dependency report over `game/bin/linuxsteamrt64`. Runs in your terminal via
  `ampersand --bootstrap` (`--skip-deps` skips the report on the CLI); the log
  in `~/.cache/sbox-ampersand/logs/` keeps the exact command while the output
  itself lives in the terminal window. Before `Setup.sh` it also gives every
  wrongly-cased image source a lowercase symlink at its identity path, which
  is where the native content compiler opens textures from (see
  **Remove case aliases**). It also refuses to start while a lowercase twin
  directory shadows a shader include directory: the shader preprocessor
  resolves `#include` through the twin and then only looks inside it, which is
  how one such twin failed 47 of 105 shaders with a misleading
  `file not found` while the include sat next to it. The preflight names the
  twin, the directory it shadows and the include that will fail; **Repair**
  (or `ampersand --repair-content-twins`) links the real includes into the twin
  under both spellings, touching no compiled output, and the twin is checked
  again after the build because the shader and content stages can create one.
  The build takes an exclusive lock
  (`<checkout>/.git/sbox-build.lock`, or the checkout root when `.git` is a
  file as in a worktree) for its whole run, so a second build -
  the GUI, `apps/build.sh`, another terminal - reports that one is already
  running instead of writing `game/bin/managed` behind it. The lock belongs to
  the build process itself: the fd is close-on-exec (and `apps/build.sh`
  closes it for its child), so the toolchain's long-lived MSBuild worker nodes
  do not inherit it and cannot make the next build read as "already running".
  It only covers builds Ampersand starts; a bare `dotnet build` of an engine or
  test project, or `sh Setup.sh` itself, does not take it, so do not run either
  while a build is in progress. If the launcher is killed while its `Setup.sh`
  keeps running, the lock goes with the launcher: the managed record is already
  invalidated then, so launches stay gated, but wait for the orphaned build
  instead of starting a second one.
  **Building that directory from two places at once can splice one assembly
  from both compiles**: the build still reports success and the launcher then
  dies two seconds into boot with `InvalidProgramException`. On success the
  tree is fingerprinted (`<checkout>/.git/sbox-managed.json`), and every launch
  checks it; a tree rewritten since - a Debug `dotnet build` of an engine or
  test project, an interrupted build, `Setup.sh` by hand - is flagged before
  the engine starts, with **Rebuild** / **Launch anyway**. Only an explicit
  click on **Launch anyway** trusts the tree (and records it as verified);
  dismissing the prompt cancels the launch. Launching is refused outright
  while a build holds the lock.
- **Remove case aliases**: deletes the case-compatibility symlinks Ampersand
  leaves in the s&box checkout (Linux only) - texture sources linked at their
  lowercased identity path, and shader includes linked into a shadowing twin.
  The dialog shows how many first; only links Ampersand journaled are removed -
  never tracked files, the engine's own links or compiled outputs - and the
  next build or repair recreates them. The same one-shot operation is
  `ampersand --revert-case-aliases`.
- **Check for missing dependencies**: `ldd` sweep of
  `game/bin/linuxsteamrt64` plus the engine's bundled .NET runtime, on the
  **host and inside steamrt4**, plus the steamrt4 compat cache status. Runs via
  `ampersand --dependency-check`. (Host-clean but container-broken is the
  classic trap. This checks both.)
- **Open log folder**: every launch and every build leaves a log in
  `~/.cache/sbox-ampersand/logs/`. Each log opens with the exact command
  that was run (`$ cd ...` plus env assignments and argv, shell-quoted) so a
  launch can be reproduced from the log alone. Terminal runs show their output
  in the window instead of the log; background runs (system-terminal toggle
  off) capture everything to the log instead. Either way the scripts narrate
  themselves (`+ [script:line] command` trace lines via `set -x` in
  `apps/_common.sh`, on by default) so inner steps — shim checks, the final
  `exec` of the engine binary with its composed `LD_PRELOAD` — are visible
  too. `SBOX_TRACE=0` silences the trace.

### CLI modes

The GUI has no output surface of its own, so reporting modes re-exec the
binary inside your terminal emulator to keep SGR colour and column alignment:

```sh
ampersand --dependency-check          # coloured host + container sweep
ampersand --bootstrap [--skip-deps]   # engine Setup.sh + ldd report
ampersand --case-aliases [ROOT] [--dry-run]  # prime the lowercase texture aliases
ampersand --content-twins [ROOT]      # report twins shadowing shader includes (exit 1 when unlinked)
ampersand --repair-content-twins [ROOT] [--dry-run]  # link their includes; no compiled output is touched
ampersand --revert-case-aliases [ROOT]  # remove exactly the links Ampersand made (both kinds)
ampersand --record-managed [ROOT]     # mark game/bin/managed as verified after a manual build
```

An unrecognised switch is an error (exit 2, with the list above), never a
request to open the window. `apps/build.sh` calls the launcher beside it, so a
`scripts/` directory copied over an older binary used to hand `--content-twins`
to a launcher that did not know it - which opened a window in the middle of the
build. The script now warns when the binary cannot answer and skips the check
instead of reading the crash as "no twins".

## How it works

- **Scripts are data.** `apps/*.sh` carry `# ampersand: name=…` /
  `# ampersand: runtime=…` headers (`ScriptMetadata.cs` reads them) and are
  copied to `<OutDir>/scripts/` at build time, or `usr/bin/scripts/` in the
  AppImage. They stay loose files so you can edit them and the next launch
  picks it up. `_common.sh` sets the HarfBuzz `LD_PRELOAD`, library paths and
  `QT_QPA_PLATFORM=xcb` workarounds; `sbox-server.sh` intentionally skips the
  desktop-UI workarounds.
- **The container is entered through Steam.** `SteamLauncherService` +
  `SteamRt4Runtime` refuse to build a container command unless the Steam client
  is up, because only its launcher service may create the user namespace. The
  service is addressed by the name it actually registered: current clients
  register one per instance (`…LaunchAlongsideSteam.Instance<pid>`) rather than
  the bare name, so the dependency report names the instance it used.
  `SteamRt4Compat` seeds `libunwind` from the host on first
  containerised launch, plus best-effort `libpcre2-16.so.0` for the editor's
  Qt tools (steamrt4's platform omits it; a miss warns but never blocks).
  Cache: `~/.cache/sbox-ampersand/steamrt4-compat`.
- **Settings** live in
  `${XDG_DATA_HOME:-~/.local}/share/sbox-ampersand/settings.json`
  (`SboxSettings.cs`); stale paths are kept for display and re-prompted.

## Project structure

```
ampersand.csproj        # net10.0 + Avalonia 12; copies apps/** → scripts/
Program.cs              # entry: UI | --dependency-check | --bootstrap | --probe-distro
App.cs / MainWindow.cs  # Avalonia app + entire window (sidebar, targets, status bar)
LaunchTarget.cs         # one script row: script file, runner, toggles
ScriptMetadata.cs       # "# ampersand: key=value" header parser
ProcessRunner.cs        # terminal-emulator or background process spawning
SystemTerminal.cs       # emulator detection + per-emulator stay-attached flags, workdir, env scrub
SteamRt4Runtime.cs      # find + validate the Steam Linux Runtime install
SteamRt4Compat.cs       # libunwind + editor-only pcre2-16 shim cache for the container
SteamLauncherService.cs # container entry through Steam's launcher service
BuildLock.cs            # single-flight build lock (flock) shared with apps/build.sh
ManagedTree.cs          # fingerprint of game/bin/managed, verified before every launch
Bootstrap.cs            # Build S&Box: twin preflight, case aliases, engine Setup.sh + ldd report
CaseAliases.cs          # lowercase image-source aliases for the Linux content build
ContentTwins.cs         # shadowing shader-include twins: preflight, repair, shared journal
DependencyCheck.cs      # host + container ldd sweep + shim report
SboxSettings.cs         # persisted s&box path + server game (~/.local/share/…)
RepoRoot.cs / AppPaths.cs / RunLog.cs / Ansi.cs / TerminalTheme.cs …
apps/                   # launch scripts (sbox.sh, sbox-dev.sh, sbox-server.sh, build.sh, _common.sh)
apps/patches/           # vendored shim sources (casefold.c; built to the ampersand cache dir on demand)
assets/                 # ampersand.desktop, AppRun, ampersand.png (logo)
install-appimage.sh       # publish → AppDir → AppImage → install
uninstall.sh              # remove an install (AppImage, entry, icon, menu pin)
bootstrap.sh            # dev build shortcut (dotnet build -c Release)
```

## Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| `Repo root not found` / stale-location warning | Pick a folder containing `game/` + `engine/` + `game/sbox`. The field accepts the root, `game/`, or the `game/sbox` file itself. |
| `Steam Linux Runtime not installed` | `steam steam://install/4183110`, then start Steam and sign in. |
| `Steam's launcher service is not available` | Container entry goes through the service Steam starts alongside the client. Start Steam and wait for it to finish signing in. |
| Launcher dies after `Warm render layers` with `InvalidProgramException`, exit 0, no stack | `game/bin/managed` was written by more than one build (Debug `dotnet build`, interrupted build, concurrent build) and one assembly is spliced. Run **Build S&Box** - the launch gate should already flag it - then launch again. Not an engine bug, and not the Steam runtime: it reproduces on the host. |
| `another build is already running` | A build already holds `<checkout>/.git/sbox-build.lock`. Wait for it (or its terminal) to finish; two writers on `game/bin/managed` is the thing the lock exists to prevent. |
| A launcher window opens while `apps/build.sh` runs, or the twin preflight reports `error: shader include directories are shadowed (see above)` with nothing above it | The `scripts/` copies are newer than the launcher binary beside them, so `--content-twins` reached a launcher that predates it and opened a window (headless: exits 134, which used to read as "twins found"). Current builds warn and skip the check; rebuild or reinstall ampersand next to the scripts. |
| `HRESULT: 0x80008088` | Missing `libunwind` inside steamrt4. Run **Check for missing dependencies** and do one containerised launch to seed the shim cache. |
| `No terminal emulator found` | Install one (`gnome-terminal`, `konsole`, `alacritty`, `kitty`, `foot`, `xterm`), or untick *Launch with system terminal* to run headless with log capture. |
| AppImage won't run (`fuse` errors) | Install `fuse2`/`libfuse2`, or extract once: `./Ampersand-x86_64.AppImage --appimage-extract` and run `squashfs-root/AppRun`. |
| Rebuild fails with `Text file busy` | The target AppImage is still running. Quit it and run `install-appimage.sh` again (the script checks for this first). |
| Content build: `LoadFromFile` failed or a texture path cannot be found | The native compiler opens a texture by its lowercased identity path while sources keep their authored case (the lowercased tree only holds compiled outputs). Ampersand aliases the sources before every build; if they were removed, run **Build S&Box** or `ampersand --case-aliases`. `SBOX_SKIP_CASE_ALIASES=1` disables the alias pass. |
| Shaders: many `fatal error: '…' file not found` (e.g. `'common/DDGI/DDGI.hlsl'`), or Build S&Box stops before `Setup.sh` with `content twins` | A lowercase twin directory (compiled `*_c` outputs) sits beside the mixed-case directory that holds the includes, so the preprocessor resolves `#include` through the twin and finds nothing. Not a missing file and not something the casefold shim can fix - it answers `open()` and this resolution never gets that far. Run **Build S&Box** (it offers **Repair**) or `ampersand --repair-content-twins`; `--revert-case-aliases` undoes it. `SBOX_SKIP_CONTENT_TWINS=1` skips only the check, not the breakage. |
| Content build: `Error opening shader "weapons/physgun/physgun-screen.shader" … Feature combo not found` | A regenerated `.vmat` names its shader through a lowercased directory while the source keeps its case (`physgun/` vs `Physgun/`). `MaterialGenerator.BuildMaterialText` writes `shaderPath.NormalizeFilename( false )`, whose `enforceLowerCase` defaults to true, so every regenerated material for a shader in a mixed-case directory reintroduces it. Upstream fix (emit the authored spelling, or resolve the identity back to the source); do **not** work around it by writing shader outputs at the lowercased identity - that creates exactly the twin above and fails 47 shaders. |
| Content build: citizen fails 4 assets, `Content: Failed`, nothing about case | The casefold shim was unavailable (no gcc, no prebuilt `libcasefold.so`). Setup says so before `Setup.sh`; install gcc or set `SBOX_CASEFOLD_SO` to a prebuilt shim. |
