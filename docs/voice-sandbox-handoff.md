# Ada — Voice / Sandbox Hang: Investigation Handoff

> Status at handoff: the **infinite hang is fixed**; the **open problem** is that Ada's
> reply doesn't reach the voice UI (stuck on "Thinking", no Ada bubble, no spoken reply).

## Product / stack

Ada is a Windows 11 desktop AI assistant: **.NET 10**, **WinForms tray app** (`Ada.App`) that hosts an
**in-process ASP.NET Core loopback server** (`Ada.Server`) + a **WebView2** UI (`src/Ada.Server/wwwroot/index.html`)
+ the **Voxa** voice framework (`Ada.Voice`). Local LLM via **Ollama** (OpenAI-compat at `127.0.0.1:11434/v1`),
currently **`gemma4:e4b`** — a *thinking / reasoning* model. Optional **"AIO sandbox"**: a Docker container
(`ada-sandbox`) exposing ~32 tools over **MCP** at `127.0.0.1:8080`.

## The fault

- **Original symptom:** with the sandbox **ON**, voice (and intermittently text) turns hang on "Thinking"
  forever, no reply. With the sandbox **OFF**, they work. Deterministic on/off correlation.
- **Current symptom (after the fix below):** the hang is gone — voice reaches the agent and the server logs
  `turn COMPLETED`. **But the reply never reaches the UI:** the user sees their transcript + a stuck
  "Thinking" spinner, no Ada bubble, no spoken reply. The agent's logged reply is suspiciously truncated
  (`"Good"`, `"I"`).

## Voice pipeline (where it matters)

```
mic → WebSocket /voice → InputRateTagProcessor → SileroVAD → WhisperCpp STT
    → Voxa TranscriptionFilter → BlankTranscriptionFilter → UseMicrosoftAgent(AIAgent)
    → SentenceAggregator (EagerFirstChunkMinChars=40) → Kokoro TTS → audio back
```
The client renders the reply from `'text'` WebSocket messages (`onVoiceMsg` in `index.html`).

## CONFIRMED root cause of the hang (fixed)

`AIAgent` (voice) and `IAdaEngine` (text) are **DI singletons built lazily on the first connection's thread**.
When the sandbox is active, that build calls `SandboxSession.WaitUntilReady` (a **synchronous `Task.Wait`**)
and composes the 32 MCP tools — **on the voice WebSocket handler thread, before the audio pipeline starts** —
stalling the connection so audio never flowed. A console CLI never reproduced it (agent already warm by
connect time; no WinForms thread pressure / startup burst).

**Fix applied (in `SandboxHostedService.WarmAgents`):** pre-build the agent + engine at startup, off the
request path, once the sandbox settles. After this, voice reaches the agent and turns complete. The working
trace then shows the full path (see below).

## CURRENT open problem (unsolved at handoff)

After the hang fix the agent runs and `turn COMPLETED` fires, but the reply text doesn't stream back to the
client (no `'text'` messages → no Ada bubble; client stuck on `'thinking'`, never transitions to
`'speaking'`/`'listening'`). Two leading hypotheses, **not yet confirmed**:

1. **`gemma4:e4b` is a thinking model** — its reasoning/content output may not be surfacing as speakable text
   frames in Voxa's pipeline.
2. **The aggregator swallows short replies** — a reply under the 40-char eager threshold may never be emitted /
   flushed at turn end. (Note: a `TurnEndSentenceAggregator.cs` now exists in HEAD — see "Repo state" — which
   appears aimed at exactly this; verify whether it resolves reply delivery.)

Output-side trace taps (`agent-out`, `tts-in`) were added to settle this — see "Next steps".

## Ruled out (with evidence)

- **The model in isolation:** raw Ollama + `gemma4:e4b` + 25 tool schemas streams a full reply in ~4s
  (thinks, then replies).
- **Tool count / MCP client layer:** decompiled `ModelContextProtocol` / `Microsoft.Extensions.AI` — schemas
  are materialized at mount, tool calls are timeout-wrapped, no per-turn blocking.
- **Silero VAD "not cached" warning:** false positive — the model ships embedded in `Voxa.Audio.SileroVad.dll`
  (~2.3 MB). Warning corrected.

## The working trace (sandbox ON, after the pre-warm fix)

```
[sandbox] active — 32 tools mounted
[warmup] agent + engine pre-built off the request path
[voice] raw-in: audio flowing (rate=24000 Hz)
[voice] VAD-out: audio flowing (rate=16000 Hz)
[voice] VAD-out: UserStartedSpeakingFrame → UserStoppedSpeakingFrame
[voice] STT-out: FINAL transcript: Good morning, Adder.
[voice] BlankFilter PASSED → agent INVOKED
[voice] turn COMPLETED — reply: Good        ← reply truncated; client never shows it
```

## Recommended next steps

1. Run the **instrumented build the user actually launches** (`artifacts/AdaDebug/RUN-WITH-LOGGING.bat`, which
   sets `ADA_LOG=Debug`). Do one voice turn. Read the new `[voice] agent-out «…»` / `[voice] tts-in «…»`
   lines: do full reply-text frames leave the agent, and do they reach TTS? That pinpoints thinking-model
   output vs. aggregator swallowing.
2. Quick A/B: switch the model to **`qwen2.5:1.5b`** (non-thinking) in Settings → Local model, restart, try
   voice. If the reply appears, it's the thinking-model output handling.
3. If aggregator: confirm the (new) `TurnEndSentenceAggregator` flushes its buffer on end-of-turn for
   sub-threshold replies, and that `index.html`'s `onVoiceMsg` then receives `'text'` and leaves `'thinking'`.

## A process pitfall that cost a lot of time

For most of the investigation the user launched `artifacts/AdaDebug` while instrumentation was being built into
**new folders** (`AdaDebug2`/`3`), so the voice tracing never executed and "no log markers" was misread as
"no activity". **Always confirm the instrumented build is the one being launched** — check the
`Content root path` line in the startup log.

## Key files

- `src/Ada.Voice/AdaVoice.cs` — voice pipeline; `MapVoxaVoice("/voice").Use(...)` resolves `AIAgent` at the
  top of the (synchronous) pipeline-build callback.
- `src/Ada.Core/SandboxSession.cs` — `WaitUntilReady` (sync `Task.Wait`).
- `src/Ada.Core/AdaAgentFactory.cs` — builds `AIAgent` (WaitUntilReady + 32 tools + skills provider).
- `src/Ada.Core/AdaCoreServiceCollectionExtensions.cs` — `BuildEngine` (text path); singleton registrations.
- `src/Ada.Server/SandboxHostedService.cs` — sandbox bring-up + `WarmAgents()` pre-build fix.
- `src/Ada.Voice/TranscriptTap.cs` — diagnostic pass-through taps (Info-level; invisible unless `ADA_LOG=Debug`).
- `src/Ada.Voice/BlankTranscriptionFilter.cs`, `TurnEndSentenceAggregator.cs` — transcript/output filters.
- `src/Ada.Server/wwwroot/index.html` — `onVoiceMsg` (client voice state machine), `sendText` (chat SSE).
- `src/Ada.Core/AgentEngine.cs` — text-path turn (120s backstop).

## Repo state at handoff

- **HEAD `1da58a5` ("fix(voice): stabilize sandbox tool turns")** contains the pre-warm fix, `TimedAIFunction`
  (60s MCP tool timeout via `Task.WhenAny`), the voice diagnostic taps, model-identity end-to-end, window
  resize (`WM_NCHITTEST`), central logging + startup diagnostics, the Silero warning fix — and a
  `TurnEndSentenceAggregator` / `ToolGroupSkill` aimed at the reply path.
- There is additional **uncommitted WIP** (e.g. `AgentEngine.cs`, `AdaEngine.cs`, `VoiceTextSanitizer.cs`) that
  is separate active work — not part of this handoff.
- Reproduce/observe: `%APPDATA%\Ada\logs\ada-<date>.log`; sandbox toggle in Settings → Workspace & sandbox
  (or the `ada-sandbox` container on `:8080`).
