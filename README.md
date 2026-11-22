# Nikomi

A small Windows tray app that answers one question: **which process is keeping the CPU busy, and what
started it?** It watches quietly in the notification area, tells you when a process has been stuck at
full load for a long time, and lets you end it by hand.

The name is Japanese (煮込み) for a dish left to simmer: this app is for the process that has been
simmering on a core for too long.

![The Nikomi window: the processes using the most CPU, with one flagged as a runaway and one as hot, and below the list the selected process's command line and the chain of processes that started it](docs/screenshot.png)

The processes in the picture are made up: it was taken with a [demo build](docs/manual.md#screenshots),
not on a real PC.

## What it does

- Shows every process with the CPU cores it is using right now, its CPU time, memory, I/O, threads,
  handles and full command line, and says what it is: the services inside a `svchost.exe`, or the
  description in the executable.
- Shows **who started it**: the whole parent chain, and whether the parent has already exited.
- Views by process, as a **tree** (each row includes what it started) or **by name** (40 browser
  processes read as one line).
- Draws the total CPU load as a **graph**, like Task Manager: live for the last 60 seconds, and for
  the last 24 hours on the History view.
- Keeps a **history** of the last 24 hours: what was busy while you were away.
- Flags a process as a **runaway** when it holds a core for a long time (0.9 cores for 10 minutes by
  default) and shows one notification for it. Names you choose are never flagged.
- Lowers a process's priority, or ends a single process or a whole process tree after a confirmation
  that lists everything that will be ended.
- Lets **AI agents** read the same figures, if you turn that on, and ask you to end a process (see
  [For AI agents](#for-ai-agents)).

## What it does not do

- It never ends a process on its own. Every end is confirmed by you, also when an AI agent asks for it.
- It needs no administrator rights and does not ask for them.
- It makes no network connections, opens no network port and collects no telemetry.
- It shows AI agents nothing until you turn agent access on.
- It installs nothing: no service, no driver, no scheduled task. It keeps a settings file, a log and
  the history (process names and core figures, no command lines) in `%LOCALAPPDATA%\Nikomi`, and one
  per-user startup entry if you turn on "Start with Windows".

## How it works

### Parts

```mermaid
flowchart LR
    subgraph OS["Windows"]
        PT["Process table"]
        NA["Notification area"]
    end

    subgraph CORE["Nikomi.Core (no UI)"]
        S["ProcessSampler<br/>reads all processes in one call"]
        M["ProcessMonitor<br/>cores, hot streaks, orphans"]
        T["ProcessTree<br/>parents and children"]
        D["ProcessDetails<br/>command line, path"]
        E["EndConfirmation<br/>what is ended, and when"]
        K["ProcessKiller<br/>ends confirmed targets"]
        H["UsageHistory<br/>the last 24 hours"]
    end

    subgraph APP["Nikomi (WinUI 3)"]
        C["AppController<br/>tray, window, settings"]
        SS["SamplingService<br/>timers"]
        AH["AgentHost<br/>agent pipe and requests"]
        TR["TrayIcon"]
        W["MainWindow"]
    end

    subgraph AGENTS["Optional, off by default"]
        AG["AI agent"]
        CLI["NikomiCli<br/>JSON commands, MCP server"]
    end

    AG --> CLI
    CLI -->|"per-user pipe"| AH
    PT --> S
    S --> M
    SS --> S
    M --> SS
    SS --> H
    SS --> C
    AH --> M
    AH -->|"end requests"| E
    W --> H
    C --> TR
    TR --> NA
    C --> W
    W --> T
    W --> D
    W -->|"shows and answers"| E
    E --> K
```

The core library has no UI and can be tested on its own, including what is ended and when: the window
only shows `EndConfirmation`'s dialog and reports the click. The app is a thin shell around it.

### One measurement

```mermaid
sequenceDiagram
    participant SS as SamplingService
    participant S as ProcessSampler
    participant M as ProcessMonitor
    participant C as AppController
    participant TR as Tray icon
    participant W as Window

    Note over SS: timer fires every 30 s in the tray, every 5 s with the window open
    SS->>S: take a sample
    S-->>SS: snapshot of all processes
    SS->>M: fold in the snapshot
    Note over M: cores = CPU time used since the last sample / time between samples
    M-->>SS: processes that just became runaway
    SS-->>C: sampled, with the new runaways
    C->>TR: tooltip with total load, red icon while a runaway exists
    opt a new runaway
        C->>TR: show one notification
    end
    opt window is open
        C->>W: refresh the list
        Note over W: command line and path are read once per process, only now
    end
```

While the window is closed, a measurement is a single system call and opens no process handles. About
every half minute one line goes into the history: the total load and the five busiest processes.

### Process status

```mermaid
stateDiagram-v2
    [*] --> Normal
    Normal --> Hot: at or above the core threshold
    Hot --> Normal: one sample below the threshold
    Hot --> Runaway: stayed hot for the set minutes
    Runaway --> Normal: one sample below the threshold
    Normal --> [*]: process exits
    Hot --> [*]: process exits
    Runaway --> [*]: process exits
```

A process is identified by its PID **and** its start time, so a PID that Windows reuses counts as a new
process. Each process triggers at most one notification. Only a stretch of a second or more is judged,
so opening the window or clicking Refresh never resets a streak.

### Ending a process

```mermaid
flowchart TD
    A["Select a process"] --> B["End process, or End process tree"]
    AG["An AI agent asks to end a process<br/>and gives its reason"] --> C
    B --> C["Dialog lists every target:<br/>name, PID, command line"]
    C -->|Cancel, the default| Z["Nothing happens"]
    C -->|Confirm| D["Next target"]
    D --> E{"Is it Nikomi itself?"}
    E -->|Yes| S1["Skip"]
    E -->|No| F{"Same PID and same start time<br/>as when you confirmed?"}
    F -->|No, or it cannot be verified| S2["Skip"]
    F -->|Yes| I{"Does Windows mark it<br/>as critical?"}
    I -->|Yes| S3["Skip"]
    I -->|No| G["End it"]
    S1 --> H["Result for every target<br/>shown in the window and logged"]
    S2 --> H
    S3 --> H
    G --> H
```

A process Windows marks as critical is never ended, because ending one crashes the system. Processes
that belong to the system or run elevated cannot be ended without administrator rights; the result says
so instead.

## For AI agents

Off until you turn it on: **Settings > Let AI agents read this data**. After that, `NikomiCli.exe`
(next to the app) answers in JSON, and also runs as a Model Context Protocol (MCP) server for agent
hosts:

```bash
app\NikomiCli.exe top --count 10 --pretty
```

```bash
app\NikomiCli.exe mcp
```

- An agent can read the load, the busiest processes and programs, process trees, runaways and the
  24-hour history.
- An agent can **ask** to end a process. Nikomi then shows you the usual confirmation, with the
  agent's reason; nothing is ended unless you confirm there.
- Command lines and file paths are left out unless you allow them separately. They can contain
  passwords, and what an agent reads goes to its AI provider.
- Both switches are a courtesy toward well-behaved agents, not a security boundary. They live in
  `settings.json`, which any program running as you can change, and an agent that can run programs
  can read the same data with Windows' own tools. What keeps processes safe is the confirmation.
- The console program talks to the app over a local named pipe. Nikomi answers only programs that run
  as your Windows account on this PC, and the console program talks only to a pipe your account owns.
  Another account on the PC can still take the pipe's name first: agents then cannot reach Nikomi,
  but that account gets nothing from it.
- Agents reach a Nikomi that runs normally. Started as administrator, Nikomi can only be reached by
  programs that run as administrator too.

Details, the full command list and how to register the MCP server are in [docs/manual.md](docs/manual.md#ai-agents).

## Requirements

- Windows 11, x64. Windows 10 version 2004 or later is the build target but is not tested.
- To run: the .NET 10 runtime.
- To build: the .NET 10 SDK.

## Build and run

```bash
dotnet publish src\Nikomi\Nikomi.csproj -c Release -o app
```

```bash
app\Nikomi.exe
```

The published folder needs no installer: it brings the Windows App SDK with it and runs as is on a PC
that has the .NET 10 runtime. Left-click the tray icon to open the window, right-click it for the menu
(Open, Settings, Mute notifications, Start with Windows, Exit). Closing the window keeps the app running
in the tray.

## Checks

```bash
dotnet run --project tests\Nikomi.Check -c Release
```

Starts its own busy-loop processes and checks the measuring, runaway, parent-chain, orphan and
end-process logic against them, plus the agent answers, the pipe and the MCP server. It ends everything
it started.

## Layout

```
src\Nikomi.Core     sampling, runaway and orphan logic, parent chains, views, history, ending, settings,
                    and what agents are answered
src\Nikomi          WinUI 3 app: tray icon, window, dialogs
src\Nikomi.Cli      NikomiCli.exe: JSON commands and the MCP server for AI agents
tests\Nikomi.Check  console program that checks the core logic with real processes
docs\manual.md      settings, startup entry, removal, and other details
```

## License

MIT. See [LICENSE](LICENSE).

The license covers this project's own code. A published build also contains Microsoft's Windows App
SDK, which stays under Microsoft's own license terms.
