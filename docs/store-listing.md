# Microsoft Store listing

The text for Nikomi's Store listing in Partner Center (English, United States), kept here so changes go
through the repository like the code. Limits are Partner Center's. The description and features are
plain text: the Store allows no HTML or links in them.

## Product name

Nikomi

## Short description

Up to 1000 characters; the Store recommends under 270.

> Find out which process keeps your CPU busy, and what started it. Nikomi watches quietly from the
> notification area, tells you when a process has been stuck at full load for a long time, and lets
> you end it after you confirm.

## Description

Up to 10,000 characters.

> Your fan is loud and your PC is slow, and Task Manager shows a process at full load that you don't
> recognize. Nikomi answers the next questions: what is it, what started it, and has it been doing
> this all afternoon?
>
> Nikomi sits in the notification area and measures every process. When one holds a CPU core for a long
> time, 0.9 cores for 10 minutes by default, Nikomi flags it as a runaway and shows one notification,
> so a stuck build, a looping script or a background updater no longer goes unnoticed.
>
> Open the window to see every process with the cores it is using right now, its CPU time, memory, I/O,
> threads, handles and full command line. Nikomi says what each one is: the services inside a
> svchost.exe, or the description in the program itself. It shows the whole chain of processes that
> started it, and whether the one that started it has already exited.
>
> Look at your processes the way that fits: one by one, as a tree where each row includes what it
> started, or by name, so 40 browser processes read as one line. A graph shows the total CPU load live
> for the last minute, and the history keeps the last 24 hours, so you can see what was busy while you
> were away.
>
> When you want to act, lower a process's priority, or end a single process or a whole process tree.
> Nikomi first lists everything that will be ended, and never ends anything on its own.
>
> For AI agents: if you turn it on, an AI assistant on your PC can read the same figures through
> Nikomi's command-line program and its Model Context Protocol (MCP) server, and ask you to end a
> process. Every such request is shown to you to confirm. Agent access is off until you turn it on,
> and command lines stay hidden from agents unless you allow them separately.
>
> Private by design: Nikomi makes no network connections, collects no telemetry and has no account. It
> needs no administrator rights. Its settings, log and 24-hour history stay in its own folder on your PC.
>
> Nikomi is free and open source under the MIT license.
>
> The name is Japanese for a dish left to simmer: this app is for the process that has been simmering
> on a core for too long.

## What's new in this version

Leave empty for the first submission.

## Product features

Up to 20, 200 characters each, without bullets (the Store adds them).

1. Shows which processes use the CPU right now, in cores, with CPU time, memory, I/O and command line
2. Shows who started each process: the whole parent chain, even when the parent has exited
3. Flags a runaway process that holds a core for a long time, with one notification
4. Names the services inside svchost.exe and describes each program
5. Views by process, as a tree, or grouped by name
6. Live CPU graph, and a 24-hour history of what was busy while you were away
7. Lowers a process's priority, or ends a process or a whole tree after you confirm
8. Optional access for AI agents through an MCP server, with every end request confirmed by you
9. No network connections, no telemetry, no administrator rights
10. Free and open source (MIT)

## Search terms

At most 7, relevant to the app, no other products' names.

1. CPU usage
2. high CPU
3. process monitor
4. CPU monitor
5. runaway process
6. process tree
7. system tray

## Screenshots

PNG, 1366 x 768 or larger (up to 3840 x 2160), at least one, up to 10. Captions up to 200 characters.
Taken with the demo build (manual, "Screenshots"), so they show made-up processes, at 1586 x 993. The
History view's 24 hours are made up too: the demo build writes them at every start. The files are in
`docs\store\`. How to submit: [store-submit.md](store-submit.md).

| File | Caption |
|---|---|
| `1-processes.png` | The processes using the CPU right now, a runaway flagged at the top, and below it the chain of processes that started it |
| `2-tree.png` | The tree view: each row includes what it started |
| `3-by-name.png` | By name: all processes with one name add up to one line |
| `4-history.png` | The last 24 hours: what was busy while you were away |

## Additional system requirements

None beyond the package's (Windows 10 version 2004 or later, x64).

## Store listing details

| Field | Value |
|---|---|
| Category | Utilities & tools |
| Price | Free |
| Privacy policy URL | https://github.com/benexus/Nikomi/blob/main/PRIVACY.md |
| Website | https://github.com/benexus/Nikomi |
| Support contact | https://github.com/benexus/Nikomi/issues |
| Copyright | Copyright (c) 2026 Nikomi contributors |

## Restricted capability: runFullTrust

Partner Center asks why the package declares `runFullTrust`. Answer:

> Nikomi is a desktop (Win32) app packaged as MSIX. It needs full trust to do what it is for: read the
> CPU, memory and I/O figures, command lines and parent processes of every process on the PC, list the
> services inside svchost.exe, and, only after the user confirms in a dialog, lower a process's
> priority or end a process. None of this is available to an app container. It makes no network
> connections.

## Age rating

The questionnaire's answers: no violence, no user-generated content shared with others, no purchases,
no location, no communication between users, no personal information shared.
