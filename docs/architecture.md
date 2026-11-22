# How Nikomi works

How the parts fit together, what one measurement does, how a process becomes a runaway, and what is
checked before a process is ended. For using the app, see the [manual](manual.md).

## Parts

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

## One measurement

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

## Process status

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

## Ending a process

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
