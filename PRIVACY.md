# Privacy policy

Nikomi, all its builds (the zip, winget and the Microsoft Store). Last updated 22 November 2026.

Nikomi collects nothing about you and sends nothing anywhere. It has no account, no telemetry, no ads,
no crash reporting and no network connections. What it reads stays on your PC.

## What Nikomi reads

To show what keeps the CPU busy, Nikomi reads, from Windows, the processes running on your PC: their
names, process IDs, CPU, memory and I/O figures, file paths, command lines, the services they host and
which process started which. A command line can contain private things, such as a file name or a
password a program was started with. Nikomi shows this to you in its window and does not store it.

## What Nikomi keeps on your PC

In its data folder, `%LOCALAPPDATA%\Nikomi` (the Microsoft Store build uses its package's own folder,
`%LOCALAPPDATA%\Packages\<package>\LocalState`):

- **settings.json**: your settings, such as the runaway rule and the names never to flag.
- **history.jsonl**: about every half minute, the total CPU load and the names, process IDs and CPU
  figures of up to five busy processes. No command lines and no paths. Lines older than 24 hours are
  dropped.
- **Nikomi.log**: what Nikomi did, such as when it started, which processes were flagged as runaways,
  which processes you ended or changed the priority of, an AI agent's stated reason when it asks to end
  one, and errors. It holds process names and IDs and the data folder's path, not command lines. Above 1 MB it is renamed to `Nikomi.log.old`, replacing the previous one, so
  at most about 2 MB is kept.

If you turn on Start with Windows, Nikomi adds one startup entry for your Windows account (a startup
task in the Microsoft Store build).

These files stay in your Windows account's own folder. Nikomi does not copy, upload or share them.

## What Nikomi sends

Nothing. Nikomi makes no network connections and opens no network port.

## AI agents

Off until you turn it on in Settings (**Let AI agents read this data**). When it is on, an AI agent you
run on this PC (such as an assistant app that uses Nikomi as an MCP server) can ask Nikomi, through
NikomiCli, for the same figures the window shows: the process list, CPU and memory figures and the 24
hours of history. Command lines and file paths are left out unless you also turn on **Also show agents
command lines and file paths**.

Only programs running under your own Windows account can ask. An agent can also ask Nikomi to end a
process; Nikomi then shows you the request and ends nothing unless you confirm it.

What an agent reads goes to whoever runs that agent, usually its AI provider, under that provider's
privacy policy. You choose which agents to run and can turn agent access off at any time; Nikomi then
answers agents with nothing but a note that access is off.

## Microsoft Store

If you install Nikomi from the Microsoft Store, Microsoft handles the download, updates and the Store
itself under the [Microsoft Privacy Statement](https://privacy.microsoft.com/privacystatement).
Microsoft may give the publisher summary figures such as install counts and crash reports, which
contain no personal information Nikomi adds. Nikomi itself adds no data collection.

## Your control

- Turn agent access off in Settings at any time.
- Delete the data folder to remove all of Nikomi's files; Nikomi starts again with default settings.
- Uninstalling the Microsoft Store build removes its folder. For the zip or winget build, delete
  `%LOCALAPPDATA%\Nikomi` after removing the app.

## Changes and contact

Changes to this policy are made in this file, with the date above, and can be followed in the
repository's history. Questions: open an issue or a discussion at
[github.com/benexus/Nikomi](https://github.com/benexus/Nikomi).
