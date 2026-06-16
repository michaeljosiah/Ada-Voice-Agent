# Ada — User Guide

**On your machine. In your voice. Under your control.**

Ada is a private, local-first personal assistant that lives in your Windows system tray. She can read
and write files, run commands, search the web, remember what matters, and talk to you — but she asks
before anything consequential, and most of your day never leaves your machine.

## Install & first run

1. Install the MSIX (or run `dotnet run --project src/Ada.App` from source).
2. Ada starts hidden in the system tray. Press **Ctrl+Alt+A** (or double-click the tray icon) to open her.
3. On first run the **setup wizard** appears:
   - Pick a **profile** — *Private* (everything local), *Balanced* (cloud for hard tasks), or *Power*.
   - **Download a local model** — pick a Gemma 3 (or Phi-4-mini) build and click *Download*. It runs
     **in-process** (no separate server) and stays fully on your machine. Gemma 3 1B is ~0.9 GB.
   - Optionally **connect a cloud provider** (Anthropic, OpenAI, Azure) for hard tasks. The API key is
     stored encrypted in the Windows credential vault — never in a file.
   - Click **Finish**. That's it — no terminal, no JSON.

With no model downloaded, Ada still runs on a local OpenAI-compatible endpoint (Ollama/LM Studio/
Foundry Local) if you have one, or a built-in echo brain. She degrades gracefully; she never breaks.
You can manage local models any time with `ada model list | pull <id> | use <id> | status`.

## Talking to Ada

- **Type** in the window, or press **Ctrl+Alt+Space** for **Voice Mode** — the orb pulses with your
  voice and hers. Press Esc or the hotkey again to stop.
- A small **route badge** shows where each reply was served: `local`, or e.g. `anthropic · code task`
  when Ada escalated to the cloud. Escalation is always visible and logged.

## Approvals — Ada asks before she acts

Reading a file or listing a folder is free. Anything that **writes, deletes, moves, runs, sends, or
spends** pops an **approval card** showing the exact command and paths. Choose **Approve**, **Approve
for session**, or **Deny**. Ada can never bypass this — even a spoken request to delete files still
needs the card. Writes are also confined to your allowed folders (your Ada workspace and Downloads by
default); system paths and Ada's own secrets are off-limits even if you approve.

Everything Ada does lands in an **audit log** (`%APPDATA%\Ada\audit.jsonl`).

## Memory

Tell Ada to *remember* something ("remember my accountant is Tunde, year-end 31 March") and she writes
a small, readable file under `%APPDATA%\Ada\memory`, indexed in `MEMORY.md`. A later session recalls it.
You can open, edit, or delete any memory by hand — nothing is hidden.

## Schedules

Ask Ada to do something on a schedule ("every weekday at 8, brief me"). Jobs run even when Ada is
closed (via a Windows scheduled task) and catch up if a run was missed. Unattended runs are
**read-only by default** — a job that wants to change something queues it for your review unless you've
granted a narrow standing permission. One **kill switch** (`ada jobs pause`) stops everything.

## Profiles & privacy

- **Private** — nothing leaves the machine; cloud escalation off.
- **Balanced** — local by default, cloud only for hard tasks, container sandbox enabled.
- **Power** — same as Balanced with the widest autonomy.

The local path is always private. Any egress — a cloud escalation, an MCP call, a web fetch — is
deliberate, shown with a route badge, and recorded.

## Uninstall

Uninstalling the MSIX removes the app. Your data under `%APPDATA%\Ada` (memory, audit, config) is
yours — delete that folder if you want nothing left behind, or keep your memory files.

## Power-user CLI

The `ada` command mirrors everything above for scripting and diagnostics: `ada doctor`,
`ada auth login`, `ada memory`, `ada jobs`, `ada config`, `ada selftest`. Run `ada` with no arguments
for the full list.
