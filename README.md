# Ada — The Personal Agent

> **On your machine. In your voice. Under your control.**

Ada is your local chief-of-staff. She lives in the Windows system tray, opens on a hotkey,
listens and speaks, and can actually *do* things on your machine — read and write files, run
commands, search the web, remember what matters — but she asks before anything consequential.
She runs a small model locally by default and escalates to a frontier model only when the task
is hard, so most of your day never leaves the machine.

Ada is a **fully standalone system** with no external dependencies on any other product. She is
also **built reusable**: a single coherent agent that grows through composable skills, mounts
external tools over MCP, and can **spawn sub-agents** to parallelize or break down complex work —
all behind one face you talk to.

📄 **The full build specification lives in [`docs/ada-agent-spec.html`](docs/ada-agent-spec.html)** —
product thesis, architecture, framework contracts, the model router, approvals, memory, the voice
stack, the sandbox/autonomy ladder, packaging, and a sequenced build plan. Written to be handed to
one engineer and built without a follow-up meeting.

---

## The four promises

| Promise | What it means |
| --- | --- |
| **Always there** | Resident in the tray, summoned by a global hotkey, ready to listen or read. No app to launch, no tab to find. |
| **Actually useful** | She doesn't just answer — she acts: files, shell, web, schedule, memory. Real hands on your machine, behind real approvals. |
| **Private by default** | The default path runs locally. Anything that leaves the machine — a cloud escalation, an MCP call, a web fetch — is deliberate, visible, and logged. |
| **Remembers you** | Durable, inspectable memory of your preferences, projects, and people, carried across sessions — and erasable in one place. |

## One agent, many hands — sub-agents

From the user's side there is one Ada — never a swarm of bots to manage. But under the hood she
can **spawn sub-agents** to get more done:

- **Decompose** a complex request into independent pieces and work them in parallel.
- **Delegate** a focused job (a deep web research pass, a bulk file transform) to a specialist
  sub-agent with its own scratch context, then fold the result back into the main thread.
- **Stay coherent** — sub-agents are an internal tool Ada calls, not a UI you operate. They
  inherit the same approval gates and sandbox boundaries as Ada herself, so parallelism never
  widens the blast radius.

## The principles every feature obeys

- **Local-first** — default inference, STT, TTS, and memory are on-device. Egress is opt-in per channel and always surfaced.
- **Ask before you act** — any tool that mutates state (write / delete / move / send / spend / install) is approval-gated. The model never bypasses a gate.
- **Describe, don't prescribe** — Ada explains and answers; she does not push you toward decisions.
- **Remember on purpose** — nothing enters long-term memory without being a readable file you can inspect.
- **One agent, many skills** — a single coherent Ada that grows through composable skills and may spawn internal sub-agents — not a swarm of bots you manage.
- **Legible by default** — what she did, what she asked, what she sent: all visible in history and the audit log.

## The persona

**Ada** — named for Ada Lovelace, the first programmer. A calm, exacting desktop chief-of-staff.
Brief by default, expansive on request. She *describes before she prescribes*, narrates what she's
about to do in one plain line, asks before anything that writes, deletes, sends, or spends, and
tells you plainly when she used the cloud or the web. Her personality isn't hardcoded — it lives
in an editable `ADA.md` persona file you can reshape.

---

## Technical foundation

| Layer | Choice |
| --- | --- |
| **Runtime** | .NET 10, Windows 11. A Generic Host app: tray icon, global hotkey, frameless WebView2 window, in-proc Kestrel on loopback. |
| **Agent loop** | [Microsoft Agent Framework](https://github.com/microsoft/agent-framework) — `AIAgent` / `ChatClientAgent` over any `IChatClient`, with documented approval and compaction primitives. We compose the loop and author Ada's host-side harness. Sub-agents compose the same way (an agent can be exposed as a tool). |
| **Voice** | [Voxa](https://github.com/michaeljosiah/voxa) — a .NET 10 voice pipeline: local WhisperCpp STT, Piper/Kokoro TTS, Silero VAD, a locked WSS protocol, barge-in purge, and OpenTelemetry hooks. |
| **Models** | A hybrid router: a local model by default, escalating to a frontier provider only on task shape. Providers (API key / Azure credential / no-auth local / custom OpenAI-compatible) are registered, not hardcoded. |
| **Extensibility** | Skills (instructions + tools + an optional MCP mount) and MCP client mounts. Any external service can attach as a drop-in skill without touching the core. |
| **Memory** | File-based, inspectable memory plus FTS5 recall — no vector index in v1. |

### The sandbox & autonomy ladder

Real autonomy — running agent-written code, crunching data, driving a browser — needs a stronger
boundary than per-action approval. Ada works inside a disposable **zone**; the only gate is the
zone boundary (writing a result to your real disk, or egress). One seam, four backends:

| Zone | What it is | Footprint |
| --- | --- | --- |
| **0 — Host** | Your real machine, gated/scoped per the approval policy. | — |
| **1 — In-process Wasm** *(default)* | Untrusted code in-process via `wasmtime-dotnet` — capability isolation, fuel + memory caps. Ships in the MSIX, runs on Windows Home, no dependency. | None |
| **2 — Local container (AIO Sandbox)** *(opt-in)* | The [AIO Sandbox](https://github.com/agent-infra/sandbox) running on **Docker** / WSL2 — real browser, shell, and data-science Python. Detected and recommended by the setup wizard. | Docker |
| **3 — Remote (Azure)** *(future)* | The same Docker AIO Sandbox on Azure Container Apps. Full power, zero local footprint. | None (cloud) |

### The four planes

The architecture is layered so every capability added later is safe by construction:

1. **Shell** — tray, hotkey, WebView2 window, onboarding.
2. **Harness** — approvals, scoping & blast radius, the sandbox/autonomy ladder, compaction, audit log.
3. **Agent** — the Agent Framework loop, the model router, tools, skills, MCP mounts, sub-agents.
4. **Model** — local-first inference with deliberate, logged cloud escalation.

---

## Build plan

Nine milestones, in order — the boring floor (skeleton, approvals, scoping) is poured first so
everything built on top is safe by construction. Each milestone ends at something you can run.

| # | Milestone | The point |
| --- | --- | --- |
| **M0** | Skeleton | Tray + hotkey + WebView2 window + loopback Kestrel; an echo proves the round-trip. |
| **M1** | Agent + local model | A real brain, locally — `ChatClientAgent` over a local `IChatClient`, streaming, first-run model download. |
| **M2** | Tools + approvals + scoping | **The safety floor** — filesystem/shell tools, the approval loop, four risk tiers, allowed roots + denylist, audit log. The in-process Wasm sandbox lands here too. Nothing destructive ships before this is right. |
| **M3** | Providers, auth & hybrid router | Connect anything; escalate on task shape. Credentials in the OS vault, route badges everywhere, "stay local" override. |
| **M4** | Memory + compaction | Remember + survive long sessions — `remember` / `recall` / `forget`, a memory browser, per-model compaction. |
| **M5** | Skills + MCP + sandbox zones | Extensibility, proven — the `ISkill` contract, Research + Desktop skills, MCP mounts, and the Docker AIO container zone. |
| **M6** | Voice | Listen + speak, locally — Voxa loopback, push-to-talk, barge-in, spoken approvals, the Voice Mode orb. |
| **M7** | Automations & schedules | Proactive, safely unattended — natural-language → cron, headless wake, read-only-by-default runs, one kill switch. |
| **M8** | Ship | Someone else can run it — setup wizard, profiles, MSIX installer, autostart, clean uninstall. |

---

## Status

✅ **M0–M8 implemented** on .NET 10 — the whole milestone ladder is built and verified headlessly via
`ada selftest` plus 55 unit tests. The GUI shell (tray + WebView2) and live voice need an interactive
Windows desktop session to exercise fully; everything else is verified in this repo's history.

| Milestone | What landed |
| --- | --- |
| **M0** Skeleton | Tray + global hotkey + frameless WebView2 window + loopback Kestrel + streaming echo |
| **M1** Local model | Agent Framework `ChatClientAgent` over a local OpenAI-compatible model; persona; route badge |
| **M2** Safety floor | fs/shell tools, four-tier approval gate, scoping + denylist, audit log, **Wasm Zone-1 sandbox** |
| **M3** Providers + router | Provider catalog, `ada auth`, DPAPI vault, hybrid router (escalate on task shape), egress logging |
| **M4** Memory | File memory + `MEMORY.md` index, **SQLite FTS5** recall, remember/recall/forget, compaction |
| **M5** Skills + MCP | `ISkill` compose, Research/Desktop skills, gated MCP mounts, **Docker Zone-2 sandbox** |
| **M6** Voice | Voxa pipeline (Silero VAD / WhisperCpp / Piper), Voice Mode orb, push-to-talk |
| **M7** Automations | NL→cron jobs, Windows Task Scheduler headless wake + catch-up, read-only-by-default, kill switch |
| **M8** Ship | Profiles, settings + first-run wizard, autostart, MSIX scaffolding, user guide |

### Build, test, verify

```sh
dotnet build Ada.slnx
dotnet test  Ada.slnx                            # 58 unit tests
dotnet run --project src/Ada.Cli -- selftest     # cumulative acceptance checks (incl. ONNX)
dotnet run --project src/Ada.App                 # the desktop app (tray + window + voice)
```

**Local model.** The recommended path is an **in-process ONNX Runtime GenAI** model (no separate
server, CPU or DirectML GPU): `ada model pull gemma-3-1b` (~0.9 GB, Gemma 3) downloads it into
`%APPDATA%\Ada\models` and Ada uses it automatically — the first-run wizard offers the same with a
progress bar. Alternatives: a local OpenAI-compatible endpoint (`ADA_PROVIDER=openai-compatible
ADA_ENDPOINT=http://localhost:11434/v1 ADA_MODEL=<model>` for Ollama / LM Studio / Foundry Local), or
a cloud provider via `ada auth`. With nothing configured Ada still runs on a built-in echo brain —
degraded, never broken.

There's also an **in-browser engine** (toggle at the top-right of the chat): the multimodal
**Gemma-4-E2B** model running client-side in the WebView via **Transformers.js** (text + image),
for quick multimodal questions. It's the model only — no agent tools/memory/approvals — and slower
than the native path, but it's the one way to run that exact multimodal Gemma 4 on-device.

### The `ada` CLI (test harness + management)

```
ada chat <message>            talk to the configured engine
ada serve [--port N]          run the loopback web UI
ada selftest                  cumulative headless acceptance checks
ada auth login <id> --key ..  connect a provider (key stored in the OS vault)
ada providers | route <msg>   inspect the catalog and routing
ada memory  list | recall | remember | forget
ada skills  list | enable | disable
ada mcp <command> [args]      mount a stdio MCP server and list its tools
ada jobs    list | add | remove | pause | resume | install | uninstall
ada run-due                   run due jobs now (what the scheduled task invokes)
ada model   list | pull <id> | use <id> | status   download/select the local ONNX model (Gemma/Phi)
ada config [profile|autostart] · ada doctor
```

See **[docs/USERGUIDE.md](docs/USERGUIDE.md)** for the end-user guide.

---

## Repository layout

```
Ada-Voice-Agent/
├─ docs/         ada-agent-spec.html (the spec) · USERGUIDE.md
├─ packaging/    AppxManifest.xml + make-msix.ps1 (MSIX installer scaffolding)
├─ src/
│  ├─ Ada.Core/   agent, harness (approvals/scope/audit/sandbox seam), hybrid router, memory,
│  │              skills, MCP mounter, automations, config — the whole brain, host-agnostic
│  ├─ Ada.Tools/  fs/shell/web tools, Wasm + Docker sandboxes, skills, schedule tools
│  ├─ Ada.Voice/  Voxa voice pipeline host
│  ├─ Ada.Server/ loopback Kestrel + the WebView2 UI assets (wwwroot)
│  ├─ Ada.App/    net10.0-windows tray + WebView2 shell (the product exe)
│  └─ Ada.Cli/    the `ada` test harness / management CLI
└─ tests/        Ada.Core.Tests · Ada.Tools.Tests
```

## Non-goals for v1

Moving money or executing transactions · a multi-agent "team" UI · cross-platform (Windows only) ·
cloud sync of memory · mobile or web clients. *Later:* wake-word, vision/screen understanding, a
vector memory index, and connecting external services (e.g. a personal finance/records backend)
through MCP as drop-in skills.

---

## License

[MIT](LICENSE) © 2026 Michael Josiah
