# Lighthouse

A lightweight Windows tray app that starts, watches and restarts the programs you want running from the moment you log in: **rclone mounts** first of all, but also any other background program (AutoHotkey scripts, FreeFileSync RealTimeSync, …).

It replaces a Startup folder full of `.bat` files and shortcuts with one tray icon that shows what is running and what is not. You can start, stop or restart each entry from the menu, and crashed processes are restarted automatically.

- **One small `.exe`** (~70 KB). It targets .NET Framework 4.8, which ships with Windows 10/11. No dependencies, no installer, no admin rights.
- **Plain-text configuration** in `Lighthouse.ini` next to the exe; rclone logs go to a `logs` folder.
- **Imports your existing startup setup**: it reads your Startup shortcuts, the registry `Run` key and the rclone mounts that are already running, and finds the actual command behind each one.
- **rclone-aware**: it controls each mount through rclone's remote-control API. It shuts mounts down cleanly, warns about pending uploads before stopping one and reports cache problems.

> The user interface (menus, dialogs, notifications) is currently in **Italian**. Code, comments and documentation are in English.

## Tray icon colors

The lighthouse icon takes the color of the most severe state among all entries:

| Color | Meaning |
|---|---|
| 🟢 Green | everything is running |
| 🟡 Yellow | warning: running, but something is wrong (see [Warnings](#warnings)) |
| 🔵 Light blue | starting or stopping |
| 🔷 Blue | a drive letter is mounted by an rclone process that Lighthouse did not start |
| 🔴 Red | error: a process exited or could not be started |
| ⚪ Grey | stopped |

Hovering over the icon shows a summary; clicking it (left or right) opens the menu, with a submenu per entry.

## Getting started

### Build

No SDK or Visual Studio needed: the C# compiler that ships with Windows is enough.

```bat
build.bat
```

This produces `Lighthouse.exe` in the same folder. The icon is drawn in code: the script builds once, asks the exe to write `Lighthouse.ico`, then builds again with the icon and the DPI-awareness manifest embedded.

### Install

1. Copy `Lighthouse.exe` to a writable folder, e.g. `C:\Tools\Lighthouse\`, and run it.
2. On the first run Lighthouse creates `Lighthouse.ini` and opens the **import** window. It lists:
   - shortcuts in the Startup folder (yours and the all-users one),
   - entries of the registry `Run` key,
   - rclone mounts that are currently running.

   For each one it finds the actual command to manage: the `rclone mount` lines inside a `.bat`, or, for documents opened by an associated program (`.ahk`, `.ffs_real`, …), the executable that is currently running them. Your own shortcuts and the mounts are preselected. The `Run` key and all-users entries are not, because they are usually installed apps that manage their own startup. **Add file…** lets you pick more `.exe`, `.bat`, script or shortcut files by hand.
3. Two options, both on by default:
   - **Copy programs and scripts into `programs\`**: each entry gets its own `programs\<name>\` folder with a copy of the executable and of the files passed as arguments; `rclone.exe` goes to `programs\rclone\`. The originals stay where they are, and the ini remembers the original command (`origin`, `originargs`, `originworkdir`). If a copy does not work, the entry's menu has **Use the original executable** ("Usa l'eseguibile originale"). Windows' own executables (e.g. `cmd.exe`) are never copied, and AutoHotkey scripts with relative `#Include`s trigger a warning.
   - **Remove the imported programs from Windows startup**: imported shortcuts are moved to `backup-avvio\` next to the exe, and removed `Run` values are written to `backup-avvio\registro-run.txt`, so nothing starts twice at logon.

   If an imported mount is already running, Lighthouse offers to restart it under its own control. A program still running from its original folder is adopted as it is and switches to the copy on its next restart.
4. Finally it asks whether to start at every logon. If you say yes it creates a scheduled task named `Lighthouse` that runs at your logon, at normal priority and without admin rights. It does not use the `Run` key, because Explorer was seen silently skipping it.

You can run the import again at any time from the menu; it only lists what is not configured yet. A program launched through a document has to be running when you import it, otherwise Lighthouse cannot tell which executable opens it.

If you move the exe, Lighthouse fixes the scheduled task the first time it is started from the new folder.

## Configuration

Everything lives in `Lighthouse.ini`. Each `[section]` is one entry in the menu. After editing the file, use **Reload configuration** ("Ricarica configurazione") from the menu. Entries you removed are stopped, new ones are started, and the rest keep running. See [`Lighthouse.example.ini`](Lighthouse.example.ini) for a complete example.

```ini
[general]
rclone=C:\rclone\rclone.exe
; rclone.conf to use (detected by the import)
rcloneconfig=C:\Users\me\AppData\Roaming\rclone\rclone.conf
; restart crashed programs automatically (up to 5 attempts, growing delay)
autorestart=1

; an rclone mount: no "exe" key
[Drive]
args=mount gdrive: G: --vfs-cache-mode=full
port=5581
autostart=1

; any other program: "exe" is the real executable, not a shortcut or a .bat that launches something else
[AutoHotkey]
exe=C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe
args="C:\Scripts\Startup.ahk"
workdir=C:\Scripts
autostart=1
```

| Key | Applies to | Meaning |
|---|---|---|
| `rclone` | `[general]` | path of `rclone.exe` |
| `rcloneconfig` | `[general]` | `rclone.conf` passed as `--config` (unless `args` already has one) |
| `autorestart` | `[general]` | `1` (default) restarts crashed entries, `0` does not |
| `rcpass` | `[general]` | rclone remote-control password: generated automatically on the first run |
| `args` | all | arguments (for rclone, without `rclone.exe`; Lighthouse adds `--rc` and `--log-file` itself) |
| `drive` | rclone | drive letter; taken from `args` if omitted |
| `port` | rclone | local remote-control port, different for each mount |
| `exe` | programs | full path of the executable |
| `workdir` | programs | working directory (default: the exe's folder) |
| `autostart` | all | `1` starts the entry when Lighthouse starts, `0` leaves it stopped |
| `origin`, `originargs`, `originworkdir` | programs | original command, written by the import when it copies files |

## How it works

### rclone mounts

Lighthouse starts rclone with `--rc --rc-addr=127.0.0.1:<port>` and `--log-file=logs\<name>.log`, plus `--config` with the configured `rclone.conf`. The rc user and password are passed through environment variables, so they never show up on the command line. The explicit `--config` matters because rclone looks for `rclone.conf` next to its own exe, and a copy in `programs\rclone` would no longer find it; the import asks the original rclone for its path (`rclone config file`).

- **Running** means the process is alive *and* the drive letter exists. A new mount gets 60 seconds to appear.
- **Stopping** goes through `core/quit` (a clean unmount), with a kill after 15 s. If `vfs/stats` reports uploads still pending, Lighthouse asks before stopping.
- Logs are rotated when they exceed 10 MB.
- A drive letter that is already mounted by an rclone process Lighthouse did not start shows as blue. **Take over** ("Prendi in gestione") kills that process and restarts the mount under Lighthouse.

### Other programs

- **Running** means the process is alive.
- **Stopping** sends `WM_CLOSE` to the program's windows first, so for example AutoHotkey runs its `OnExit` handler, then kills it after 5 s.

### Adoption, no duplicates

Managed processes keep running when Lighthouse is closed (unless you choose **Exit and stop the mounts**). When Lighthouse starts again it adopts them instead of starting them twice. It finds mounts through their rc port and other programs by matching executable and arguments among the running processes (via WMI).

### Automatic restart

When a process exits unexpectedly, Lighthouse restarts it up to 5 times, waiting 10, 20, 40, 80 and 160 seconds. After that the entry stays red until you start it again.

### Warnings

Lighthouse never parses program logs: it only reports what it can check by itself.

- **All entries**
  - *Repeated crashes*: 2 or more unexpected exits in the last 30 minutes.
  - *Not responding*: one of its windows does not answer an empty message (`WM_NULL`) within 3 s, on two checks in a row. This is an active probe, because Windows only flags a window as hung when input arrives.
- **rclone mounts**, by asking rclone on the rc port every 30 s:
  - *files in error* in the VFS cache,
  - *stalled uploads*: uploads queued but no bytes transferred for 15 minutes,
  - *cache out of disk space*,
  - *rclone not answering* commands.

A new warning shows a notification. The entry's menu has **Mark as seen** ("Segna come visto"), which turns it green again until the problem changes or comes back. rclone warnings and the not-responding warning clear by themselves when the problem goes away.

### Diagnostics

- `logs\status.txt` holds the current state of every entry, rewritten whenever it changes.
- Command-line helpers that run without the tray:
  - `Lighthouse.exe --scan result.txt [folder]` writes what the import would find, optionally including one extra folder of shortcuts;
  - `Lighthouse.exe --import-test test.ini [folder]` imports the preselected entries into a test ini, copying files next to that ini, without touching Windows startup.
- `Lighthouse.exe path\to\other.ini` runs with a different configuration file.

Lighthouse also resets its own priority to normal at startup: Task Scheduler starts tasks at reduced priority, and child processes such as rclone would inherit it.

## Project files

| File | Contents |
|---|---|
| `Lighthouse.cs` | entry point, tray icon and menu, process supervision and health checks, run at logon, icon drawing |
| `Import.cs` | first run and import of existing Windows startup entries |
| `build.bat` | builds `Lighthouse.exe` with Windows' own `csc.exe`, generating and embedding the icon |
| `Lighthouse.manifest` | DPI awareness, so the tray icon stays sharp with display scaling |
| `Lighthouse.example.ini` | example configuration |

## Requirements

- Windows 10 or 11 (.NET Framework 4.8, included)
- [rclone](https://rclone.org/) for mounts, with [WinFsp](https://winfsp.dev/) installed as rclone requires on Windows

## License

[MIT](LICENSE)
