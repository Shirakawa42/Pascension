# Installed Windows game: console/focus investigation, 2026-09-27

Status: historical hangs confirmed; the reported flashing-console/unfocus failure has not yet been reproduced. No machine-wide setting, service, antivirus or startup entry was changed.

## Evidence

- Installed path: `E:\Bureau\pascension-windows-v1.0.4`; it contains the current native Shards.AI assembly. The current game source has no AI subprocess launcher. The game-side Process.Start uses found are in the updater (user-triggered install, or macOS helper paths).
- Windows Application events identify AppHangB1 / event 1002 for this exact executable on September 16, 26 and 27. The recurring hang signature is 24f6. These are historical observations, not a resolved diagnosis.
- September 18 events separately identify access-violation crashes in Windows.Gaming.Input.dll. This is a lead, not evidence that the same component causes the focus hang.
- Read-only inventory found no scheduled task matching the game/old training identifiers. An Ollama startup entry remains. It was not removed: presence does not establish a causal link.
- The initial 45-second trace had no running game. A subsequent 300-second trace captured two user-launched installed-game processes and 2,144 window samples, including extended time unfocused. No IsHungAppWindow sample was true. Two 250-ms WM_NULL timeouts occurred during startup, not a sustained hang.
- The faster 180-second trace used native process snapshots and visible-window enumeration. It observed 181 window snapshots: no visible ConsoleWindowClass, ghost window, or hang. The game stayed foreground during this particular trace.
- Captured console-host process starts included children of VS Code/Git/WSL and Unity compilation/MCP. A console host process is not necessarily a visible console. No captured visible console has been attributed to the game.

## Reproduction loop

`Tools/Diagnostics/trace-fast.ps1` now supports non-administrator WinEvent create/show hooks as well as polling, avoiding loss of very short visible-console lifetimes between samples. It records PID, executable name, parent PID, window class, focus and bounded window-message responsiveness; no raw command lines or account data. Run hidden Windows PowerShell with `-File <script> -Seconds 300`. Exit 2 means sustained hang/ghost; exit 3 means no game observed.

The user was asked to launch the installed game and leave it unfocused for about 15 seconds, reporting whether the flashing console occurs in this run. A failing captured run is still needed to correlate the visible console's creator with the unresponsive window and then test a specific fix. The investigation must not equate an Ollama shortcut, an antivirus DLL or an old input crash with the cause without that evidence.

## Independent AI correction

Hero drafting is fixed and validated independently. A Windows player was built successfully at `Builds/WindowsDraftFix`. The prepared installer waits for the running game to exit normally, retains the complete previous installation, then installs the fresh build. `Tools/Diagnostics/draft-install-status.json` is the authoritative installation state; no game process is killed. This draft correction is not represented as a fix for the unresolved Windows hang.
