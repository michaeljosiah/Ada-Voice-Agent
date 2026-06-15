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

🚧 **Pre-implementation.** The specification is complete; the .NET solution has not been scaffolded yet.

**Verified**

- Microsoft Agent Framework C# surface (`AIAgent`, `ChatClientAgent`, `IChatClient`, `AgentSession`, approvals, compaction).
- Voxa .NET 10 host API (`AddVoxa`, `MapVoxaVoice`, `UseDefaults`), local speech tier, WSS transport + barge-in.
- A Zone-1 in-process Wasm sandbox spike (`wasmtime-dotnet`, .NET 10, no Docker/Hyper-V): capability isolation, fuel runaway-trap, and memory cap enforced; real TS/JS and stdlib-Python ran.

**Next**

- Scaffold `Ada.sln`, the project layout, and central package pinning.
- Pin exact Agent Framework, Voxa, MCP, model-provider, and Windows packaging versions.
- Build M0 → M2 (skeleton → local agent → the approval/scoping safety floor).

---

## Repository layout (planned)

```
Ada-Voice-Agent/
├─ docs/
│  └─ ada-agent-spec.html     # the full build specification
├─ src/
│  ├─ Ada.App/                # net10.0-windows — tray host + WebView2 shell (exe)
│  ├─ Ada.Core/               # net10.0 — agent, harness, router, memory, skills, sub-agents, audit
│  ├─ Ada.Tools/              # net10.0 — shell, fs, web, clipboard, screenshot, scheduler
│  ├─ Ada.Mcp/                # net10.0 — MCP client mounts
│  ├─ Ada.Voice/              # net10.0 — in-proc ASP.NET Core + Voxa hosting
│  └─ Ada.Web/                # WebView2 UI assets (chat + settings)
├─ spikes/                    # proving grounds (e.g. the Wasm sandbox spike)
├─ tests/
├─ LICENSE                    # MIT
└─ README.md
```

## Non-goals for v1

Moving money or executing transactions · a multi-agent "team" UI · cross-platform (Windows only) ·
cloud sync of memory · mobile or web clients. *Later:* wake-word, vision/screen understanding, a
vector memory index, and connecting external services (e.g. a personal finance/records backend)
through MCP as drop-in skills.

---

## License

[MIT](LICENSE) © 2026 Michael Josiah
