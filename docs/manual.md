# Nikomi

A small Windows 11 tray app that shows which processes are keeping the CPU busy and lets you end them by
hand. It never ends anything on its own, needs no admin rights and reads no temperatures.

WinUI 3 (Windows App SDK 2.7, Mica backdrop, Fluent controls), .NET 10, x64. Unpackaged: the published
folder brings the Windows App SDK with it and runs as is on a PC that has the .NET 10 runtime, with
nothing to install.

## Download

Nikomi is in the [Microsoft Store](https://apps.microsoft.com/detail/9n6kvm71lrr5), packaged; how
that version differs is under [Microsoft Store build](#microsoft-store-build).

Each [release](https://github.com/benexus/Nikomi/releases/latest) has a ready-built zip,
`Nikomi-<version>-win-x64.zip`, with the same files as the build below in a `Nikomi` folder. It needs
the .NET 10 runtime. Unzip it anywhere and read `Nikomi\` wherever this manual says `app\`.

## Build

Needs the .NET 10 SDK (10.0.404 or newer).

```bash
dotnet publish src\Nikomi\Nikomi.csproj -c Release -o app
```

This produces `app\Nikomi.exe` plus the Windows App SDK runtime next to it (about 95 MB, 275 files),
and `app\NikomiCli.exe`, the console program for [AI agents](#ai-agents).
Publish again after a code change; quit Nikomi first (tray menu > Exit), or the files are in use.

The release zip is made by a script, which publishes into a clean folder, leaves out the debug symbols
and prints the zip's SHA-256 for the release notes and the winget manifest:

```powershell
powershell -File tools\pack-release.ps1
```

It writes `publish\release\Nikomi-<version>-win-x64.zip`, taking the version from
`Directory.Build.props`. Its entry names use forward slashes; winget's archive check takes a zip with
backslashes in them for malware.

## Start it

```bash
app\Nikomi.exe
```

It opens the window and puts an icon in the tray (click the `^` arrow on the taskbar if Windows hides it;
drag it onto the taskbar to keep it visible).

- **Left-click** the tray icon: open the window. Closing the window only hides it; Nikomi keeps
  watching in the tray. The first time you close it, a notification says so.
- **Right-click** the tray icon: Open, Settings..., Mute notifications for 1 hour, Start with Windows,
  Exit.
- Starting `Nikomi.exe` a second time just opens the window of the one already running.
- `Nikomi.exe --background` starts it in the tray without opening the window (this is what
  "Start with Windows" uses).

## Start with Windows, and turning that off

Turn it **on** with either:

- tray icon > right-click > **Start with Windows** (gets a check mark), or
- the window's Settings (gear button) > **Start with Windows** > Save.

That adds the value `Nikomi` = `"<path>\Nikomi.exe" --background` to
`HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run`. It is per user and needs no admin
rights. It points at the exe where it is now, so if you move the `app` folder, turn it off and on again.

Turn it **off** with any of:

- the same menu item or Settings toggle again;
- Task Manager > **Startup apps** > `Nikomi` > Disable (Windows then skips it while the value stays);
- Settings > Apps > Startup > `Nikomi` off.

If you turned it off in Task Manager or in Windows Settings, Nikomi shows it as off and does not
switch it back on by itself: the tray item and the Settings dialog open the Startup apps page, where
you turn it on again.

## Remove it completely

1. Turn off Start with Windows (see above). If the app folder is already gone, delete the value instead:

   ```bash
   reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "Nikomi" /f
   ```

2. Tray icon > right-click > **Exit**.
3. Delete this folder (with `app` inside it).
4. Delete `%LOCALAPPDATA%\Nikomi` (settings, log and history).

Nothing else is written anywhere: no services, no scheduled tasks, no installer entries.

## What it shows

- **Cards:** total CPU load with a graph of the last 60 seconds (measured every second while the window
  is open; the process list keeps its own, slower rhythm), logical cores (1.00 core = one core fully busy), uptime and boot time,
  process count and the interval measured, orphans (processes whose parent has exited, which is common
  and harmless on its own; "List them" opens that view), and runaways with the current rule.
- **Views:**
  - *Top by CPU* and *All*: one row per process.
  - *Tree*: each process followed by what it started. A row's figures include everything under it.
  - *By name*: one row per process name with the sum over all of them (40 browser processes read as
    one line). Select a row and choose **Show these processes** to list them.
  - *Hot & runaway* and *Orphans*.
  - *History*: one line about every half minute for the last 24 hours, with the total load and the
    five busiest processes in that stretch. A graph of the 24 hours sits on top; a gap in it means
    Nikomi was not running or the PC was asleep. Point at the graph to see that moment's line.
- **Filter box** (Ctrl+F): matches name, PID, description, service names or command line. In the Tree
  view a match keeps the rows above it, so you see where it sits. Click a column header to sort.
  **Pause** stops the list from re-sorting while you read it; **Refresh** (F5) measures again now.
- **Each row:** status badge, name, PID, cores in use now, accumulated CPU time, memory (private
  working set, the figure Task Manager shows), I/O (bytes read and written per second: files, network
  and pipes together), description (the services running in the process, or what the executable says
  it is), "started by" (nearest parent first: `parent (pid) < grandparent (pid) < ...`, ending in
  `[exited N]` when an ancestor is gone), the full command line, start time, threads and handles. The
  list scrolls sideways when the window is narrower than the columns.
- **Details pane** for the selected process: executable path, what the file says it is and who made it
  (taken from the file; no signature is checked), the services in it, full command line, committed
  memory, priority, session, the whole parent chain and how many running processes it started.

"Cores now" is the CPU time a process used during the last interval divided by the interval's length,
so 1.00 means it kept one logical core busy the whole time. With the window open the interval is the
refresh time (5 s by default); while closed, it is the background interval (30 s).

Dates and times follow your regional format in Windows.

## Runaway rule

A process is **hot** while it stays at or above *RunawayCores* (default **0.9** cores) in every
measurement, and a **runaway** once it has stayed hot for *RunawayMinutes* (default **10**). One dip
below the threshold resets it. A measurement shorter than one second (the window opening, a click on
Refresh) is too noisy to judge and is counted into the next one instead.

When a new runaway appears, the tray icon turns red with a `!` and Windows shows one notification that
names it (clicking the notification opens the window on it). The same process (same PID and start time)
never triggers a second notification, even if it cools down and heats up again. When no runaway is
left, the icon turns back to normal.

Two ways to quieten it:

- **Never-flag list:** names on it (a game, a compiler) are never flagged or notified, however long
  they stay hot; the row says "Not flagged" instead. Add a name by right-clicking a process > **Never
  flag ... as runaway**, or edit the list in Settings (one name per line, with or without `.exe`).
- **Mute notifications for 1 hour** in the tray menu. Runaways are still marked and the icon still
  turns red; only the notification is held back.

## Actions on a process

Right-click a row, or use **More** in the details pane:

- **Copy command line**, **Copy executable path**, **Open file location**.
- **Set priority** (High, Above normal, Normal, Below normal, Low): a gentler step than ending a
  process that is only in the way. Realtime is left out on purpose.
- **Never flag ... as runaway** (see above).
- **End process...** (Delete) and **End process tree...** (Shift+Delete).

## Ending processes

Select a row, then **End process...** (just that one) or **End process tree...** (it and every
process it started, and theirs). A confirmation lists every process by name, PID and
command line before anything happens; Cancel is the default button. The result appears in a bar above
the list and in the log. Before ending, Nikomi checks that each PID still belongs to the same process
(exactly the same start time), so a PID that Windows has reused for something else is never touched;
a process it cannot verify is skipped. A process Windows marks as critical (ending one crashes the
system) is always skipped, and the confirmation says so beforehand. It cannot end elevated or system
processes without admin rights; it says so in the result instead. Changing a priority goes through the
same checks.

## AI agents

An AI agent (Claude Code, Claude Desktop, or anything else that can run a program or speak MCP) can read
what Nikomi measures and ask you to end a process. It is **off** until you turn it on:

- Settings > **Let AI agents read this data**.
- Settings > **Also show agents command lines and file paths** is a second, separate switch. Leave it off
  unless the agent needs them: a command line can contain a password or a token, a path contains your
  user name, and whatever an agent reads is sent to its AI provider.

Agents use `NikomiCli.exe`, which sits next to `Nikomi.exe`. It asks the running app; if the app is
not running it measures by itself (about one second), which is enough for reading but not for ending.

### Commands

Every answer is one JSON object; an answer with an `error` field exits with code 1.

| Command | What it answers |
|---|---|
| `status` | total CPU load, the runaway rule, current runaways |
| `top [--count N] [--sort S] [--name NAME]` | the busiest processes, or all processes of one program |
| `names [--count N] [--sort S]` | one row per program name, summed over its processes |
| `process --id ID` or `--pid N` | one process: what it is, its figures, what started it |
| `tree [--id ID \| --pid N]` | the top of every process tree, or one process and all it started |
| `hot` | processes at or above the runaway threshold and for how long |
| `history [--minutes N] [--name NAME]` | average and peak load, busiest programs, latest lines |
| `end --id ID --reason "TEXT" [--tree]` | asks you to end a process (see below) |
| `end-result` | the outcome of the last `end` that answered `pending` |

`--sort` is `cores` (default), `memory`, `cpu_time` or `io`. `--pretty` indents the JSON.
`--data-dir <folder>` talks to the app that was started with the same option.

Each process in an answer has an `id`: its PID and start time. `end` takes only that id, never a bare
PID, so a PID that Windows has reused in the meantime is not hit.

### As an MCP server

`NikomiCli.exe mcp` is a Model Context Protocol server on standard input and output. It offers the
commands above as tools (`cpu_status`, `top_processes`, `processes_by_name`, `process_details`,
`process_tree`, `hot_processes`, `cpu_history`, `end_process`, `end_result`). Register it in your agent
host as a stdio server, with the full path to the exe. For example, in Claude Code:

```bash
claude mcp add nikomi -- "C:\path\to\app\NikomiCli.exe" mcp
```

In a host that is configured with a JSON file, the entry is the command `C:\path\to\app\NikomiCli.exe`
with the single argument `mcp`.

### When an agent asks to end a process

Nothing is ended by the agent. Nikomi opens its window, shows a notification, and puts up the same
confirmation as for your own click, with two additions: it says that an AI agent is asking, and it shows
the agent's reason in the agent's own words (Nikomi does not check that text, but removes characters
that would hide words or reverse their order, and cuts it at 400 characters). **Do not end** is the
default button, and the end button works only once the confirmation has been in view for a second, so a
click meant for something else cannot confirm a request that appears under the pointer.

- If you confirm, the same checks run as for any end (same PID and start time, never a critical process,
  never Nikomi itself), and the agent gets the result for each process.
- If you decline, the agent is told so. After any request you did not confirm (declined, not answered,
  or not shown because another Nikomi dialog was open), further requests are refused for one minute,
  so an agent cannot keep bringing the window forward.
- Turning agent access off closes a confirmation an agent asked for, as withdrawn; nothing is ended.
- If you have not answered after 45 seconds, the agent is told the request is `pending` and can ask for
  the outcome later. The confirmation closes unanswered after 5 minutes; nothing is ended then.
- Requests for a critical process, for Nikomi itself or with an id that no longer exists are refused
  without a dialog. Only one request is open at a time.

### What to know before turning it on

- Process names, descriptions and command lines are written by the programs themselves. A program can
  put text there that reads like an instruction to the agent. Every answer tells the agent to treat such
  text as data, and the confirmation is there because that cannot be relied on.
- The console program and the app talk over a named pipe that exists only while agent access is on.
  Its access list lets only your Windows account in. On top of that, Nikomi checks each connection and
  answers only a program that runs as your account on this PC; it does not serve a pipe that another
  account created first; and the console program talks only to a pipe your account owns. Another
  account on the same PC can still take the pipe's name before Nikomi does. Agents then cannot reach
  the app (the console program measures by itself, and `end` says Nikomi is not running), and the log
  says so, but that account reads nothing and can ask nothing. Connections from another computer are
  refused. No network port is opened.
- Run Nikomi normally, not as administrator, when agents should reach it. An elevated Nikomi's pipe
  belongs to the Administrators group, so an agent's console program that is not elevated cannot open
  it: it measures by itself, and `end` says Nikomi is not running. This is on purpose. Letting programs
  without administrator rights talk to an elevated Nikomi would hand them what only an administrator
  may see (other elevated programs' command lines) and a way to ask for elevated processes to be ended.
  The log says so when an elevated Nikomi turns agent access on.
- The log records that agents are reading (at most one line a minute), and every end request with the
  agent's reason and your answer.
- Turning the switch off only stops Nikomi from answering. An agent that can run programs on your PC
  can still use Windows' own tools, such as `tasklist` and `taskkill`; that is governed by the agent
  host's permissions, not by Nikomi.
- The two switches are a courtesy toward well-behaved agents, **not a security boundary**. They are
  stored in `settings.json` in the data folder, which any program running as you can edit, an agent with
  file access included. When the app is not running, `NikomiCli.exe` goes by that file alone, and the app
  takes a changed file on its next start without asking. What an agent cannot get around is the
  confirmation: nothing is ended through Nikomi unless you click the end button.

## Settings

Gear button in the window, or tray menu > Settings...

| Setting | Default | Meaning |
|---|---|---|
| RunawayCores | 0.9 | cores a process must hold to count as hot |
| RunawayMinutes | 10 | minutes it must stay hot to be a runaway |
| BackgroundSeconds | 30 | measurement interval while the window is closed |
| WindowSeconds | 5 | refresh interval while the window is open |
| TopCount | 40 | rows in "Top by CPU" |
| IgnoreNames | empty | process names that are never flagged runaway |
| AlwaysOnTop | off | the window stays above other apps' windows |
| AgentAccess | off | AI agents may read the figures and ask to end a process |
| AgentCommandLines | off | agents also see command lines and file paths |

They are stored in `%LOCALAPPDATA%\Nikomi\settings.json`. Next to it:

- `Nikomi.log`: startup, runaway notifications, tray icon changes, everything ended or re-prioritised
  by hand, and what AI agents asked for. It rolls over at 1 MB.
- `history.jsonl`: the History view's lines, so they survive a restart. Each line holds a time, the
  total load and up to five process names with their PID and cores. No command lines. It rolls over
  at 2 MB.

`Nikomi.exe --data-dir <folder>` uses another folder, which is handy for a test run with a lowered
threshold without touching your real settings.

## Cost

One measurement is a single `NtQuerySystemInformation` call (about 10 ms for ~550 processes) with no
process handles opened. While the window is closed only that runs, every 30 s, plus one appended line
of history. Command lines, parent chains, file descriptions and the service list are read only while
the window is open.

## Checks

```bash
dotnet run --project tests\Nikomi.Check -c Release
```

Starts its own busy-loop processes (one core each) and checks, against the same code the app uses:
cores measured ≈ 1.00, the runaway flag with a lowered threshold and that it is reported only once,
total load, parent chain, command line, the extra columns against what .NET reports, services,
changing a priority, the reused-PID guard, ending a whole tree, orphan detection, and the
Start-with-Windows registry logic (against throwaway keys, never the real ones). With made-up samples
it also checks the one-second rule, the never-flag list, the history and the tree and by-name views.
For AI agents it checks the JSON answers (including that command lines stay hidden until allowed), the
pipe between the console program and the app, the MCP server's messages, and that nothing is answered
while agent access is off. It ends everything it started, and prints `All checks passed` at the end.

To try the runaway flag in the real app, start a busy loop and Nikomi with a test data folder whose
`settings.json` has `"RunawayMinutes": 0.5`:

```bash
tests\Nikomi.Check\bin\Release\net10.0-windows10.0.19041.0\Nikomi.Check.exe busy demo --child
```

Then end it from the window with **End process tree...**.

## Microsoft Store build

The Store gets Nikomi as an MSIX package, built from the same code:

```bash
dotnet build src\Nikomi\Nikomi.csproj -c Release -p:Store=true
```

The package lands in `publish\store\Nikomi_<version>_x64_Test\Nikomi_<version>_x64.msix`; that `.msix` is
the file to upload. The scripts next to it are the build's own, for installing it by hand, and the warning
about `mspdbcmf.exe` only means no symbols package is made. The package brings .NET with it (about 70 MB), declares
`src\Nikomi\Package\Package.appxmanifest`, and is unsigned: the Store signs it. The build keeps its own
`bin\store` and `obj\store` folders, so it never mixes with the normal build.

Packaged, a few things work differently, because Windows keeps a packaged app's writes to its own copy:

- **Data folder:** the package's own folder (`%LOCALAPPDATA%\Packages\<package>\LocalState`) instead of
  `%LOCALAPPDATA%\Nikomi`. A Store Nikomi and a zip Nikomi keep separate settings and history, and can run
  side by side. Uninstalling the package removes its folder.
- **Start with Windows:** a startup task declared in the package, shown in Settings > Apps > Startup like
  any other. It starts Nikomi in the tray, always with the default data folder: a Nikomi started with
  `--data-dir` cannot pass its folder on, unlike the Run key entry of the zip. When an organization's policy
  turns the task on or off, Settings shows it and leaves it alone.
- **Commands:** `NikomiCli` is the console program for agents, from any terminal; the app itself opens
  from the Start menu (a package app may have only one such command). Register the MCP server as `NikomiCli mcp`; only the packaged NikomiCli finds the packaged
  app's data and pipe. A zip NikomiCli stays with the zip app's folder even when a packaged program, such
  as an MCP host from the Store, starts it.

To try the package on this PC before submitting it (Developer Mode on), register the built folder:

```powershell
Add-AppxPackage -Register src\Nikomi\bin\store\Release\net10.0-windows10.0.19041.0\win-x64\AppxManifest.xml
```

and remove it again with `Get-AppxPackage IndieSoftware.Nikomi | Remove-AppxPackage`. A copy registered
this way has the Store's identity, so remove it before installing Nikomi from the Store.

The manifest's `Identity` `Name` and `Publisher` and its `PublisherDisplayName` are the values Partner
Center shows under Product identity (`IndieSoftware.Nikomi`, published by Indie Software), and must stay
exactly so. `Nikomi.Check` checks that the manifest's version is the one in `Directory.Build.props`. The whole submission, step by step, is in
[store-submit.md](store-submit.md); the listing's text is in [store-listing.md](store-listing.md).

## Screenshots

The picture in the README shows made-up processes, so that it gives away nothing about a real PC. A
demo build produces them:

```bash
dotnet publish src\Nikomi\Nikomi.csproj -c Release -p:Demo=true -o demo
```

```bash
demo\Nikomi.exe
```

Everything a demo build shows about the PC and its processes comes from `src\Nikomi\DemoSource.cs`:
the process list, command lines, paths, services, the number of cores, the uptime, the load graph and
the history. The list opens on a runaway that is 47 minutes old and a process that became hot two minutes ago; the
live graph fills in over the first minute. The History view shows a made-up last 24 hours, written anew
at every start so it ends at the current time. A demo build shows no runaway notification, and keeps its
settings, log and history in `%TEMP%\Nikomi-demo`, so it can run next to a real Nikomi without
touching its files. Ending a made-up process or changing its priority does nothing, because no real
process has both its PID and its start time.

A normal build contains none of this: `DemoSource.cs` is compiled only with `-p:Demo=true`.

## Layout

```
src\Nikomi.Core     sampling, runaway/orphan logic, parent chains, views, history, services, ending, settings, log,
                    agent answers, the pipe and the MCP server
src\Nikomi          WinUI 3 app: tray icon, window, dialogs
src\Nikomi.Cli      NikomiCli.exe: JSON commands and the MCP server for AI agents
tests\Nikomi.Check  console program that checks the core logic with real busy processes
tools               pack-release.ps1: the release zip
```
