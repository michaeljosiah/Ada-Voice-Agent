# Ada — The Personal Agent

> **On your machine. In your voice. Under your control.**

Ada is your local chief-of-staff. She lives in the Windows system tray, opens on a hotkey,
listens and speaks, and can actually *do* things on your machine — read and write files, run
commands, search the web, remember what matters — but she asks before anything consequential.
She runs a small model locally by default and escalates to a frontier model only when the task
is hard, so most of your day never leaves the machine.

She is built reusable: the same harness that powers your personal assistant is a foundation
future features can stand on. And she is designed for one specific future — when the **AONIK
MCP** lands, a single skill slots in and Ada can discuss your cross-border financial commitments
with you, in her own voice, with your data still under your control.

📄 **The full build specification lives in [`docs/ada-agent-spec.html`](docs/ada-agent-spec.html)** —
product thesis, architecture, framework contracts, the model router, approvals, memory, the voice
stack, packaging, and a sequenced build plan. Written to be handed to one engineer and built
without a follow-up meeting.

---

## The four promises

| Promise | What it means |
| --- | --- |
| **Always there** | Resident in the tray, summoned by a global hotkey, ready to listen or read. No app to launch, no tab to find. |
| **Actually useful** | She doesn't just answer — she acts: files, shell, web, schedule, memory. Real hands on your machine, behind real approvals. |
| **Private by default** | The default path runs locally. Anything that leaves the machine — a cloud escalation, an MCP call, a web fetch — is deliberate, visible, and logged. |
| **Remembers you** | Durable, inspectable memory of your preferences, projects, and people, carried across sessions — and erasable in one place. |

## The principles every feature obeys

- **Local-first** — default inference, STT, TTS, and memory are on-device. Egress is opt-in per channel and always surfaced.
- **Ask before you act** — any tool that mutates state (write / delete / move / send / spend / install) is approval-gated. The model never bypasses a gate.
- **Describe, don't prescribe** — Ada explains and answers; she does not push you toward decisions — especially, later, about money.
- **Remember on purpose** — nothing enters long-term memory without being a readable file you can inspect.
- **One agent, many skills** — a single Ada with a growing, composable skill set, not a swarm of bots.
- **Legible by default** — what she did, what she asked, what she sent: all visible in history and the audit log.

## The persona

**Ada** — named for the firstborn (Igbo) and for Ada Lovelace. A calm, exacting desktop
chief-of-staff. Brief by default, expansive on request. She *describes before she prescribes*,
narrates what she's about to do in one plain line, asks before anything that writes, deletes,
sends, or spends, and tells you plainly when she used the cloud or the web. Her personality isn't
hardcoded — it lives in an editable `ADA.md` persona file you can reshape.

---

## Technical foundation

| Layer | Choice |
| --- | --- |
| **Runtime** | .NET 10, Windows 11. A Generic Host app: tray icon, global hotkey, frameless WebView2 window, in-proc Kestrel on loopback. |
| **Agent loop** | [Microsoft Agent Framework](https://github.com/microsoft/agent-framework) — `AIAgent` / `ChatClientAgent` over any `IChatClient`, with documented approval and compaction primitives. We compose the loop and author Ada's host-side harness. |
| **Voice** | [Voxa](https://github.com/michaeljosiah/voxa) — a .NET 10 voice pipeline: local WhisperCpp STT, Piper/Kokoro TTS, Silero VAD, a locked WSS protocol, barge-in purge, and OpenTelemetry hooks. |
| **Models** | A hybrid router: a local model by default, escalating to a frontier provider only on task shape. Providers (API key / Azure credential / no-auth local / custom OpenAI-compatible) are registered, not hardcoded. |
| **Memory** | File-based, inspectable memory plus FTS5 recall — no vector index in v1. |

### The four planes

The architecture is layered so every capability added later is safe by construction:

1. **Shell** — tray, hotkey, WebView2 window, onboarding.
2. **Harness** — approvals, scoping & blast radius, sandbox/autonomy ladder, compaction, audit log.
3. **Agent** — the Agent Framework loop, the model router, tools, skills, MCP mounts.
4. **Model** — local-first inference with deliberate, logged cloud escalation.

---

## Build plan

Nine milestones, in order — the boring floor (skeleton, approvals, scoping) is poured first so
everything built on top is safe by construction. Each milestone ends at something you can run.

| # | Milestone | The point |
| --- | --- | --- |
| **M0** | Skeleton | Tray + hotkey + WebView2 window + loopback Kestrel; an echo proves the round-trip. |
| **M1** | Agent + local model | A real brain, locally — `ChatClientAgent` over a local `IChatClient`, streaming, first-run model download. |
| **M2** | Tools + approvals + scoping | **The safety floor** — filesystem/shell tools, the approval loop, four risk tiers, allowed roots + denylist, audit log. Nothing destructive ships before this is right. |
| **M3** | Providers, auth & hybrid router | Connect anything; escalate on task shape. Credentials in the OS vault, route badges everywhere, "stay local" override. |
| **M4** | Memory + compaction | Remember + survive long sessions — `remember` / `recall` / `forget`, a memory browser, per-model compaction. |
| **M5** | Skills + MCP | Extensibility, proven — the `ISkill` contract, Research + Desktop skills, MCP mounts. The AONIK seam stubbed, not built. |
| **M6** | Voice | Listen + speak, locally — Voxa loopback, push-to-talk, barge-in, spoken approvals, the Voice Mode orb. |
| **M7** | Automations & schedules | Proactive, safely unattended — natural-language → cron, headless wake, read-only-by-default runs, one kill switch. |
| **M8** | Ship | Someone else can run it — setup wizard, profiles, MSIX installer, autostart, clean uninstall. |

---

## Status

🚧 **Pre-implementation.** The specification is complete and grounded against the source repos;
the .NET solution has not been scaffolded yet.

**Verified**

- Microsoft Agent Framework C# surface (`AIAgent`, `ChatClientAgent`, `IChatClient`, `AgentSession`, approvals, compaction).
- Voxa .NET 10 host API (`AddVoxa`, `MapVoxaVoice`, `UseDefaults`), local speech tier, WSS transport + barge-in.
- A Tier-1 in-process Wasm sandbox spike (`wasmtime-dotnet`, .NET 10, no Docker/Hyper-V): capability isolation, fuel runaway-trap, and memory cap enforced; real TS/JS and stdlib-Python ran.

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
├─ src/                       # Ada.App (shell) + Ada.Core (harness, agent, router) — to come
├─ spikes/                    # proving grounds (e.g. the Wasm sandbox spike)
├─ LICENSE                    # MIT
└─ README.md
```

## Non-goals for v1

Moving money or executing transactions · the AONIK finance skill itself · a multi-agent "team"
UI · cross-platform (Windows only) · cloud sync of memory · mobile or web clients. *Later:*
wake-word, vision/screen understanding, a vector memory index.

---

## License

[MIT](LICENSE) © 2026 Michael Josiah
