<img src="src/AgentReclaim.App/Assets/AgentReclaim-256.png" alt="" width="96" align="right">

# AgentReclaim

A Windows tray app that watches what AI coding tools (Claude Code, Codex) are running, finds what they leave behind, and frees it safely.

Nothing is stopped, throttled or deleted unless you click a button or turn on an automatic rule. Every automatic rule is **off by default**.

## Features

| Area | What it does |
|---|---|
| **Leftovers** | Process trees an agent started and abandoned (the agent or its shell exited, the child kept running). **Stop…** sends Ctrl+C first where that's safe, then force-stops only the processes you approved. |
| **Live sessions** | Every running `claude.exe` / `codex.exe` (or npm-installed agent under `node`) with its process tree, memory, CPU and listening ports. |
| **Findings** | Piles of old idle children under a live agent (e.g. hundreds of `node_repl.exe` under a Codex app-server), and agents whose own memory keeps climbing. |
| **Cleanup** | WSL/Docker VM memory: shut WSL down, or switch automatic memory reclaim on (`dropCache`, WSL's default) or off (`autoMemoryReclaim=disabled`) in `.wslconfig` (backup kept each time). Disk: old Claude Code / Codex temp folders (to the Recycle Bin), npm/pip/uv caches (via the tool's own clean command), clean stale git worktrees (`git worktree remove`, which refuses if there are changes). |
| **Auto rules** (opt-in) | Stop leftovers that were *seen* under an agent, have no open connections and have been idle N minutes. Throttle (lower priority + EcoQoS) build/test tools that hog the CPU under an agent. |
| **Activity** | Every action, with **Relaunch** to start a stopped process again in its original folder. |
| **Claude Code hook** (opt-in) | A `SessionEnd` hook that tells the app a session ended, so its leftovers only need a short grace period. |
| **MCP server** | Read-only tools (`resource_overview`, `list_session_processes`, `list_leftovers`) so an agent can ask what's using the machine. It cannot stop anything. |
| **Appearance** | Light, dark or Auto (follows Windows' app mode, live). Settings → Appearance. |
| **Tray** | Memory gauge (green / amber / red) with an orange dot when there are leftovers; start with Windows (opt-in). |
| **Updates** | Checks GitHub Releases daily. Choose *Don't check*, *Tell me* (default) or *Install automatically*. Packages are verified against the SHA-256 published with the release, the exe is swapped and the app restarts in the tray. If the new version doesn't start and report ready within 90 s, the previous exe is put back and restarted, and that version is not auto-installed again. Settings → Updates, or tray → Check for updates. |
| **Debug log** | `%LOCALAPPDATA%\AgentReclaim\logs`, one file per day, 7 days kept, 10 MB/day cap. Info level by default; Settings → Diagnostics → Debug logging adds every poll and decision. **Copy diagnostics** copies versions and settings (no process data) for bug reports. |

## Safety model

- **Attribution.** Each poll (every 3 s) walks every parent chain; a parent only counts if it started before the child (Windows reuses PIDs). Everything under a live agent is recorded in `lineage.json`, including descendants that inherit ownership after their parent exits. An empty or failed process listing raises an error instead of wiping that history.
- **Never listed as leftovers:** agents themselves (including detached daemons), anything hosting a live agent (e.g. a plugin broker), and AgentReclaim.
- **Stopping** re-reads the live tree after you confirm and refuses if it gained processes. It holds identity-checked handles and terminates only the approved instances; there is no recursive kill. Ctrl+C is sent by a separate helper process, and only when every process on that console was approved, so your own terminal is never interrupted.
- **Logging.** An "intent" line is written before acting and a "done" line after; if the intent can't be logged, nothing is stopped.
- **Protected:** IDEs, terminals, WSL/Docker, system processes.
- **Auto-stop** only applies to leftovers with an observed (not guessed) owner, no established connections, and passing the same safety checks. It stops at most one tree per fresh snapshot, re-reads network connections right before stopping, and does nothing if connection data is unavailable. After a hook-reported session end, the grace period counts from the end itself.
- **Worktree cleanup** only offers worktrees with no changes and no ignored files except build output (`node_modules`, `bin`, `obj`, …), checks again right before removal, and says that removal is permanent.
- **Relaunch** only runs in the original folder; if it's gone, it refuses rather than guess.
- **Updates** only replace AgentReclaim's own single-file exe, and are checksum-verified. The checksum comes from the same GitHub release, so it catches corrupt downloads, not a compromised account; sign releases for that.

## Build & run

Requires the .NET 9 SDK.

```
dotnet build
dotnet test
dotnet run --project src/AgentReclaim.App          # opens the window
dotnet run --project src/AgentReclaim.App -- --tray # starts hidden in the tray
```

Closing the window keeps it in the tray; use the tray menu → Exit.

## Install

Two downloads on each [release](https://github.com/baslawson/AgentReclaim/releases), both self-contained (no .NET needed):

- **`AgentReclaim-<version>-win-x64-setup.exe`**: installs for your user only (no admin) to `%LOCALAPPDATA%\Programs\AgentReclaim`, with a Start menu entry, optional desktop shortcut and start-with-Windows, and an uninstaller in Settings → Apps. Uninstalling stops the app, removes start-with-Windows and the Claude Code hook only if they point at the installed copy, and asks before deleting your settings and history.
- **`AgentReclaim-<version>-win-x64.zip`**: portable. Unzip anywhere and run.

Both update themselves the same way. Neither is code-signed yet, so SmartScreen warns on first run.

### Release build

```
./build/publish.ps1                          # tests, then the single-file exe, zip, setup exe and .sha256 files in dist/
./build/publish.ps1 -CertThumbprint <sha1>   # also signs the exe and setup (needs the Windows SDK's signtool)
```

The setup exe needs Inno Setup 6 (`winget install JRSoftware.InnoSetup`); without it the script builds the zip only. The exe (~71 MB) needs no .NET install. Unsigned builds trigger a SmartScreen warning on other machines. Sign with your own code-signing certificate before distributing.

### Command-line modes

| Command | Used by |
|---|---|
| `AgentReclaim.exe [--tray]` | You / Start with Windows |
| `AgentReclaim.exe mcp` | MCP clients, e.g. `claude mcp add agentreclaim -- "C:\path\AgentReclaim.exe" mcp` |
| `AgentReclaim.exe hook session-end` | Claude Code's SessionEnd hook (install it from Settings) |
| `AgentReclaim.exe send-ctrl-c <pids> <approved>` | Internal helper for graceful stops |
| `AgentReclaim.exe.old update-supervise …` | Internal: rolls back an update that fails to start |
| `AgentReclaim.exe uninstall-cleanup` | Internal: run by the uninstaller |

## Data

`%LOCALAPPDATA%\AgentReclaim`: `settings.json`, `lineage.json` (attribution memory), `actions.jsonl` (every action with command line and folder), `logs\`.

For testing: `AGENTRECLAIM_DATA_DIR` moves the data folder (and gives that instance its own single-instance lock and pipe, so it can run next to your real copy), and `AGENTRECLAIM_UPDATE_API` points the update check at another URL.

## Layout

| Path | Contents |
|---|---|
| `src/AgentReclaim.Core/Collection` | Process snapshot via Toolhelp + handle queries (no WMI, ~40 ms for ~800 processes), TCP table |
| `src/AgentReclaim.Core/Analysis` | Agent signatures, lineage store, session/orphan analyzer, stale-pile and leak findings |
| `src/AgentReclaim.Core/Actions` | Safety policy, terminator, Ctrl+C helper, throttler, relauncher, action log |
| `src/AgentReclaim.Core/Rules` | Opt-in auto rules (pure decisions, unit-tested) |
| `src/AgentReclaim.Core/Cleanup` | WSL status/shutdown/.wslconfig, disk cleanup scanner |
| `src/AgentReclaim.Core/Integration` | Hook, notification pipe, hook installer, MCP server, autostart |
| `src/AgentReclaim.Core/Updates`, `Diagnostics` | GitHub release check, verified self-update, debug log |
| `src/AgentReclaim.App` | WPF window, tray icon, background monitor, entry point |
| `tests/AgentReclaim.Core.Tests` | 89 tests: analyzer, safety, auto rules, leaks, config editing, MCP, logging, updates, Codex-review regressions, live-machine checks |
| `build/publish.ps1`, `build/installer.iss` | Release build and per-user installer |
| `build/make-icon.ps1` | Draws the app icon (`src/AgentReclaim.App/Assets`) |

## Not done yet

- Codex has no session-end hook equivalent wired up (Codex leftovers are still found by lineage and idle time).
- MSIX packaging / winget submission (both need a signing certificate and a hosted download).
