# Ziggurat

A layered AI agent for the home — Telegram, WebChat, and voice satellites on one stack, built on MCP and OpenRouter
LLMs.

Every tier is a separate MCP server. Channel servers carry the conversation (Telegram, WebChat, voice, Azure Service
Bus, scheduling), tool servers mount as virtual filesystems, and one agent works across all of them: the media
library, the smart home, schedules, timers, and the printer.

## Features

- **Multi-Agent Support** - Run multiple agents from a single container, each with unique configurations
- **Channel Architecture** - Transports (WebChat, Telegram, ServiceBus, Voice, Scheduling) run as independent MCP channel servers; agents publish a single-source catalog to each channel via `register_agents`
- **WebChat** - Browser-based chat with real-time streaming, topic management, and multi-agent selection
- **Telegram Multi-Bot** - Each agent gets its own Telegram bot with inline keyboard tool approvals
- **Azure Service Bus** - Queue-based integration for external systems
- **Voice Satellites** - Hands-free voice assistant on real hardware: `nabu-satellite` is a single static Rust binary for Raspberry Pi with on-device "ok nabu" wake-word detection (openWakeWord via tract, no Python), zero-lag pre-roll, audio cues, button support, and a status LED ring (reSpeaker XVF3800) showing distinct listening/thinking/speaking phases; the voice channel dials each satellite over the Wyoming protocol, transcribes with Whisper, runs the agent, and streams Kokoro TTS back (via Lemonade). A speaker-verification gate drops non-enrolled voices (TV, guests) before STT and routes recognized speakers' identities into the message, and noisy captures can be cleaned by a target-speech-extraction sidecar before transcription
- **Conversation Persistence** - Redis-backed chat history survives application restarts
- **Tool Approval System** - Approve, reject, or auto-approve AI tool calls with whitelist patterns
- **Download Completion Alerts** - The library server is dual-role (tool/filesystem server and channel): a background watcher polls qBittorrent and pushes a `channel/message` to the originating conversation when a download finishes — no client-side tracking or resubscription needed, and alerts survive restarts because routing snapshots live in Redis
- **Downloads Overlay** - In-flight downloads surface inside the media filesystem: a virtual `/media/downloads/<id>/status.json` reports live progress, and deleting `/media/downloads/<id>` cancels the download and cleans up its files
- **Web Search & Browsing** - Search the web via Brave Search API; browse pages with persistent sessions using accessibility tree snapshots and element-ref interactions via Camoufox (anti-detect browser)
- **Home Assistant Control** - Drive a Home Assistant instance as a virtual filesystem (`filesystem://ha`, mounted at `/ha`) — entities and areas are directories, `state.json` is the live state, and each available service is a `<service>.sh` action file invoked via `fs_exec`. A directory-listing setup index is injected into the system prompt so the LLM can pick devices without exploring
- **Sandbox Execution** - Isolated Linux container with bash + Python execution via `fs_exec` (60s default / 30min max timeout, 64 KB output cap), persistent `/home/sandbox_user` for installed packages, ephemeral system dirs, outbound network only
- **Virtual Filesystem** - Unified filesystem across MCP servers via `filesystem://` resource discovery, with domain tools for read, create, edit, glob, search, move, copy, delete, exec, and file info. Each mount lives in a separate MCP server (its own container, host, or even a different machine reachable over HTTP), and the agent can move or copy files **across mounts** — between containers or hosts — using chunked blob streaming under the hood
- **Memory System** - Built-in proactive memory with LLM-based extraction from windowed conversation context, vector recall, and periodic consolidation (dreaming)
- **Scheduled Tasks** - Cron and one-shot agent prompts managed as a virtual filesystem (`filesystem://schedules`); a dedicated scheduling channel server fires due schedules and fans the result out to chosen delivery channels
- **Printing** - Print to a real IPP/CUPS printer as a virtual filesystem (`filesystem://print-queue`, mounted at `/print-queue`) — copying or creating a file into the mount immediately submits it to the configured printer, removing a still-pending file cancels the job, and finished jobs auto-disappear from the listing. The accepted formats are configurable (`SupportedFormats`, default plain text, JPEG, and the printer-native raster/page formats); anything else is rejected on copy-in. Text is CRLF-normalized and images are scaled to fit the page
- **Timers & Alarms** - Countdown timers as a virtual filesystem (`filesystem://timers`) that ring on the voice satellites through the voice hub's HTTP endpoints; clock-time alarms and reminders ride a dedicated Home Assistant calendar bridged to the voice announce endpoint, with insistent repeat-until-acknowledged announcements
- **OpenRouter LLMs** - Supports multiple models (GLM, GPT, Gemini, Claude, etc.) via OpenRouter API
- **MCP Architecture** - Modular tool servers and channel servers for extensibility
- **Streaming Pipeline** - Concurrent message processing with GroupByStreaming and Merge operators
- **Observability Dashboard** - PWA dashboard showing token costs, tool analytics, error rates, per-turn latency, voice-turn breakdowns, schedule history, memory analytics, and live service health
- **Subagent Delegation** - Parent agents can spawn ephemeral subagents for parallel or heavy tasks
- **Docker Compose Stack** - Full self-hosted stack: qBittorrent, Jackett, Plex, FileBrowser, Home Assistant, Music Assistant, Lemonade (STT/TTS), Camoufox, Redis, and Caddy

## Architecture

```
┌─────────────────┐     ┌─────────────────┐     ┌─────────────────┐     ┌─────────────────┐     ┌─────────────────┐
│    WebChat      │     │    Telegram     │     │  Azure Service  │     │ Schedule timer  │     │ Voice satellite │
│ (Blazor WASM)   │     │    (Bots)       │     │       Bus       │     │   (internal)    │     │ (nabu, Rust/Pi) │
└────────┬────────┘     └────────┬────────┘     └────────┬────────┘     └────────┬────────┘     └────────┬────────┘
         │                       │                       │                       │                       │ Wyoming/TCP
         ▼                       ▼                       ▼                       ▼                       ▼
┌─────────────────┐     ┌─────────────────┐     ┌─────────────────┐     ┌─────────────────┐     ┌─────────────────┐
│ McpChannel      │     │ McpChannel      │     │ McpChannel      │     │ MCP Scheduling  │     │ McpChannel      │
│ SignalR         │     │ Telegram        │     │ ServiceBus      │     │ (channel role — │     │ Voice (Wyoming  │
│ (MCP Server)    │     │ (MCP Server)    │     │ (MCP Server)    │     │  also in below) │     │ hub + STT/TTS)  │
└────────┬────────┘     └────────┬────────┘     └────────┬────────┘     └────────┬────────┘     └────────┬────────┘
         │    channel/message    │                       │                       │                       │
         │    send_reply         │                       │                       │                       │
         │    request_approval   │                       │                       │                       │
         └────────────┬──────────┘───────────────────────┘───────────────────────┘───────────────────────┘
                      │
                      ▼
              ┌───────────────┐
              │  Agent Core   │
              │ (MCP Client)  │
              └───────┬───────┘
                      │
      ┌───────────────┬───────────────┬───────────────┬───────────────┬───────────────┬───────────────┬───────────────┬───────────────┐
      ▼               ▼               ▼               ▼               ▼               ▼               ▼               ▼               ▼
┌─────────────┐ ┌─────────────┐ ┌─────────────┐ ┌─────────────┐ ┌─────────────┐ ┌─────────────┐ ┌─────────────┐ ┌─────────────┐ ┌─────────────┐
│ MCP Library │ │  MCP Vault  │ │ MCP Sandbox │ │MCP WebSearch│ │MCP Idealista│ │MCP HomeAsst.│ │MCP Schedules│ │ MCP Printer │ │ MCP Timers  │
│ filesystem: │ │ filesystem: │ │ filesystem: │ │             │ │             │ │ filesystem: │ │ filesystem: │ │ filesystem: │ │ filesystem: │
│   //media   │ │   //vault   │ │  //sandbox  │ │             │ │             │ │    //ha     │ │ //schedules │ │//print-queue│ │  //timers   │
└──────┬──────┘ └─────────────┘ └──────┬──────┘ └──────┬──────┘ └──────┬──────┘ └──────┬──────┘ └─────────────┘ └──────┬──────┘ └──────┬──────┘
       │                               │               │               │               │                               │               │
┌──────┴──────┐                 ┌──────┴──────┐ ┌──────┴──────┐ ┌──────┴──────┐ ┌──────┴──────┐                 ┌──────┴──────┐ ┌──────┴──────┐
│ qBittorrent │                 │Linux sandbox│ │  Camoufox   │ │  Idealista  │ │HomeAssistant│                 │  IPP/CUPS   │ │  voice hub  │
│   Jackett   │                 │bash + python│ │ (anti-det.  │ │     API     │ │ (REST API)  │                 │   printer   │ │ (announce)  │
│ FileBrowser │                 │   fs_exec   │ │   browser)  │ └─────────────┘ └─────────────┘                 └─────────────┘ └─────────────┘
└─────────────┘                 └─────────────┘ └─────────────┘

              ┌────────────────────────────────────┐
              │  Virtual Filesystem (domain)       │
              │  Discovers filesystem:// resources │
              │  Mounts → Registry → Domain tools  │
              └────────────────────────────────────┘
              ┌────────────────────────────────────┐
              │       Memory (built-in)            │
              │  Extract → Store → Recall → Dream  │
              │         Redis Vector Store         │
              └────────────────────────────────────┘
                     ┌─────────────────────────────────┐
  metrics:events     │       Observability             │
  (Redis Pub/Sub)───▶│  Collector → Redis Aggregation  │──▶ Dashboard (PWA)
                     │  REST API + SignalR Hub         │
                     │  Serves Dashboard.Client        │
                     └─────────────────────────────────┘
```

### Channel Protocol

Each channel MCP server exposes a standard protocol. Wire serialization is centralized in `ChannelProtocol` — a shared `JsonSerializerOptions` plus typed notification/param records (`ChannelMessageNotification`, `ChannelCancelNotification`, `RegisterAgentsParams`, `RequestApprovalParams`) so every channel speaks the same contract.
- **Inbound**: `channel/message` notification — pushes user messages to the agent
- **Inbound**: `channel/cancel` notification — cancels an in-flight turn
- **Outbound**: `send_reply` tool — agent streams response chunks back to the user
- **Outbound**: `request_approval` tool — interactive tool approval or auto-approval notification
- **Outbound**: `create_conversation` tool — agent-initiated conversations
- **Outbound**: `register_agents` tool — agent publishes its agent catalog to the channel on connect/reconnect

Channels use the registered catalog as the single source of truth for available agents (SignalR rebroadcasts it as `OnAgentsUpdated`, so the WebChat agent list updates live) instead of maintaining a duplicated `Agents` config. A server can be **dual-role** — both a channel and a tool/filesystem server (`mcp-scheduling` is both, and so is `mcp-library`, whose channel role pushes download-completion alerts); for those, the channel-protocol tools are hidden from the LLM.

New transports can be added by deploying a new channel MCP server — zero agent code changes needed.

### Virtual Filesystem

Filesystems are not bound to the agent process. Each MCP tool server can expose its local storage as a `filesystem://<name>` resource — for example `mcp-library` advertises `filesystem://media`, `mcp-vault` advertises `filesystem://vault`, and `mcp-sandbox` advertises `filesystem://sandbox`. Backends need not be disk-backed: `mcp-homeassistant` advertises `filesystem://ha` (entities/areas/actions), `mcp-scheduling` advertises `filesystem://schedules` (scheduled tasks), and `mcp-printer` advertises `filesystem://print-queue` (a print spool that submits to a real printer), all implementing the same `IFileSystemBackend` contract and returning typed `FsResult<T>`. At session start, `McpFileSystemDiscovery` enumerates these resources across every connected MCP server and registers each one in a `VirtualFileSystemRegistry` with longest-prefix path resolution.

Because each mount is just an MCP endpoint over HTTP, filesystems can be deployed wherever is convenient:

- **Same container**: trivial — the MCP server reads/writes its local disk.
- **Different containers on the same host**: the default Docker Compose layout. Each MCP server is its own container with its own volume mount, and the agent reaches them over the compose network (e.g. `http://mcp-vault:8080/mcp`).
- **Different machines**: point `mcpServerEndpoints` at any reachable URL. A NAS, a remote workstation, or a cloud VM that runs the same MCP server image becomes a first-class mount with no agent code changes — it's just another HTTP endpoint speaking the MCP `fs_*` protocol.

The agent exposes 10 unified domain tools — `VfsTextRead`, `VfsTextCreate`, `VfsTextEdit`, `VfsGlobFiles`, `VfsTextSearch`, `VfsMove`, `VfsCopy`, `VfsRemove`, `VfsExec`, `VfsFileInfo` — that dispatch through the registry, so the LLM works with absolute paths like `/media/movies/...` or `/vault/notes/...` and never has to know which backend hosts which mount. `VfsGlobFiles` supports brace-expansion patterns (`**/*.{jpg,png}` expands to the union of `**/*.jpg` and `**/*.png`), and every backend returns glob matches as full virtual paths.

**Cross-filesystem operations.** `VfsMove` and `VfsCopy` detect when source and destination live on different mounts and fall back to a streaming pipeline: the source MCP server's `fs_blob_read` streams chunks, which are written to the destination via `fs_blob_write`, then the source is removed (for move). This works regardless of where the two backends are deployed — moving a file from a sandbox container on one host to a vault directory on another host is the same operation as moving it between two paths in the same container. Best-effort per-entry results are reported for directory recursion so partial failures are visible.

New filesystems are added by deploying any MCP server that exposes a `filesystem://` resource — no agent code changes needed.

### Outposts

Every mount above is a container in the compose stack, configured into an agent by hand. An **outpost** is the exception: a filesystem on a real machine that announces itself, so the agent can read and edit the files on a laptop for as long as that laptop is switched on, with nothing added to any configuration file.

It is a self-contained single-file `linux-x64` binary. Copy it onto a machine and run it:

```bash
SHAREDSECRET=... ./McpServerOutpost --name laptop --dir ~/project --jailed --hub http://agent-host:5000
```

`--name` is the mount point the agent addresses (`/laptop/...`). `--dir` is where files land and commands run, and is the mount's declared workspace. `--jailed` confines every operation to it — the mount root is still the machine's root, so a path keeps the same name either way and asking for something outside comes back as a refusal rather than an empty result. `--exec` allows commands and is off unless asked for. `--advertise` overrides the address worked out from the route toward the hub, `--port` the documented default of 8099, and `--ext` the shared list of extensions that count as text.

The binary registers with the agent's HTTP API on startup, refreshes every 30 seconds against a 90 second expiry, and takes its registration back when stopped — so a machine that is switched off disappears at once, and one that loses power lapses on its own with nothing on the hub having to notice. Both directions present one shared secret — the machine when it registers, the agent when it dials — so a port on somebody's own computer is not an open door. Set `SHAREDSECRET` in the machine's environment and `OUTPOSTS__SHAREDSECRET` on the hub; it is deliberately not a flag, because a command line is visible to every process.

On a machine that has this checkout, `scripts/run-outpost.sh` publishes the binary and runs it in one step, filling in `--name` (the hostname), `--dir` (the directory you ran it from) and `--hub` for the flags you did not type, and reading `SHAREDSECRET` from the environment or `OUTPOSTS__SHAREDSECRET` from `DockerCompose/.env`. Adding `--install-service` writes a systemd **user** unit that starts the binary with exactly those resolved flags at every boot:

```bash
./scripts/run-outpost.sh --name <name> --dir /home/<user>/ --exec --install-service
```

A user unit rather than a system one because an outpost publishes a person's own filesystem and the service has to reach precisely the files its operator reaches; lingering is enabled so it comes up with nobody logged in. The unit is named after the outpost (`ziggurat-outpost-<name>.service`), so a machine can run a jailed outpost and an unjailed one side by side, and it runs a copy of the binary under `~/.local/share/ziggurat-outpost` rather than the publish output — republishing over the file a running service is executing would fail with "text file busy". The secret stays out of the unit for the reason it stays off the command line, in a mode-600 `EnvironmentFile`. `--uninstall-service` takes it all back off again. WSL and an ordinary Linux machine take the same path; a WSL distro with systemd switched off gets `[boot] systemd=true` written into `/etc/wsl.conf` and a note to run `wsl --shutdown`, and one on default NAT networking is warned that the address the outpost works out for itself is not one the hub can dial back — pass `--advertise`.

An agent sees outposts only if `usesOutposts` is set on it; nothing is opted in by default. Live registrations join its endpoint list when a session is built, so a machine that appeared mid-conversation is there from the next session. A machine that is asleep costs only its own mount — a dynamically registered endpoint that will not answer is logged and dropped, while a configured one still fails the session loudly ([ADR 0027](docs/adr/0027-static-endpoints-fail-dynamic-ones-are-dropped.md)). An outpost whose name is already another mount's is *shadowed*: the existing mount wins, and the next keepalive tells the machine why it never appeared.

### Scheduled Tasks

Scheduling is a dual-role MCP server (`mcp-scheduling`) rather than an in-process agent feature. The agent connects to it both as a tool/filesystem server and as a channel:

- **As a filesystem** (`filesystem://schedules`, mounted at `/schedules`) the agent manages schedules with the ordinary `Vfs*` tools. Each agent gets a directory; a schedule lives at `/schedules/<agentId>/<scheduleId>/schedule.json` containing `{prompt, cron|runAt, userId?, deliverTo?}` — exactly one of `cron` (recurring 5-field UTC cron) or `runAt` (one-shot ISO-8601 UTC datetime, auto-deleted after it fires). `agent_info.json` describes each agent and read-only `status.json` reports `createdAt`/`lastRunAt`/`nextRunAt`. Running `VfsExec` with `run_now.sh` on a schedule directory fires it immediately.
- **As a channel** a background dispatcher polls Redis for due schedules and emits a `channel/message` to the agent. The agent runs the prompt and fans the result out to the schedule's `deliverTo` channels (e.g. `["signalr", "telegram"]`), minting conversations as needed.

The `scheduling_prompt` MCP prompt teaches the LLM the `/schedules` idiom.

### Home Assistant

Home Assistant is exposed as the `filesystem://ha` mount (path `/ha`) by `mcp-homeassistant`, so the agent drives it with the ordinary `Vfs*` tools rather than a bespoke entity/service API. The `HaFileSystem` backend (`Domain/Tools/HomeAssistant/Vfs/`) implements `IFileSystemBackend` and returns typed `FsResult<T>` — no disk, just live HA REST calls overlaid on a cached catalog of entities/areas/services.

Layout:

- `/ha/entities/<class>/<object-id>_(<friendly-slug>)/` — one directory per entity, grouped by domain (e.g. `/ha/entities/light/kitchen_(kitchen)/`). The directory name carries the friendly name in `_(...)` suffix form so `glob` alone identifies a device.
- `/ha/areas/<area-slug>/<entity-id>_(<friendly-slug>)/` — the same entities grouped by room. `<area-slug>` is the HA-assigned area `id` (a frozen slug, not the renameable display name).
- Inside each entity directory: `state.json` (live state + attributes, fetched fresh on read) and one `<service>.sh` per available action.

Workflow: `VfsGlobFiles` to find the entity → optionally `VfsTextRead` `state.json` for an input attribute → `VfsExec` `<service>.sh --help` to learn arguments → `VfsExec` `<service>.sh --arg value` from the entity directory to act. Action results come back via `exitCode` (0 success, 1 HA rejected, 2 bad argument, 124 timeout, 127 unknown action) with `{ok, changed[], response}` in `stdout`.

A directory-listing setup index (`HomeAssistantSetupSummary`) is appended to the `home_assistant_guide` MCP prompt at fetch time so the LLM sees every device path without globbing first. The MCP server only exposes the filesystem (`fs_glob`, `fs_read`, `fs_info`, `fs_search`, `fs_exec`) — there are no entity-specific or service-specific tools.

### Printing

Printing is another non-disk MCP filesystem server (`mcp-printer`) exposing `filesystem://print-queue` (mounted at `/print-queue`). It mirrors the `ScheduleFileSystem`/`HaFileSystem` pattern — a domain `PrinterQueueFileSystem : IFileSystemBackend` engine (`Domain/Tools/Printing/Vfs/`) plus thin `fs_*` MCP wrappers — so the agent prints with the ordinary `Vfs*` tools and **zero agent code changes**.

- **Submit by copy/create.** Copying or creating a file into `/print-queue/<filename>` immediately submits it to the configured printer. Bytes ingest through `fs_blob_write` (chunk streaming), are written to a disk-backed spool (`IPrintSpool`/`PrintSpool` under the spool volume, keyed by filename), and are handed to `IPrinterClient.SubmitAsync` for an assigned job id.
- **Cancel by remove.** `VfsRemove` on a still-active job calls `IPrinterClient.CancelAsync`; if the job has already finished it is a no-op. `move` and `exec` are intentionally unsupported.
- **Accepted formats.** Configurable via `SupportedFormats` (default plain text, JPEG, and the printer-native raster/page formats — PWG Raster, Apple URF, PCL); any other format (PNG, PDF, Office docs, etc.) is rejected on copy-in rather than printed as garbage. `SupportedFormats` is the single source of truth — the `printing_prompt` and the print-queue resource description advertise exactly what it lists, so the agent is always told precisely what it can print. Text payloads are CRLF-normalized to stop staircase printing, and images are submitted with `print-scaling=fit` so they fit the page with margins. The `printing_prompt` MCP prompt teaches the LLM to convert anything else to a supported format first.
- **Status & retention.** `/print-queue/status.json` is a read-only view of every queued job and its state (queued / pending / processing). Finished jobs auto-disappear from the listing during reconciliation; there is no print history.

The IPP transport is `IppPrinterClient` (`Infrastructure/Clients/Printer/`), a `SharpIppNext` + `HttpClient` adapter against the configured `PRINTERURI` (a CUPS server or a direct-IPP printer — same protocol), mapping the `Print-Job`, `Get-Jobs`, and `Cancel-Job` IPP operations.

### Timers & Alarms

Countdown timers are another non-disk MCP filesystem server (`mcp-timers`) exposing `filesystem://timers` (mounted at `/timers`). Creating `/timers/<id>/timer.json` arms a countdown, reading the read-only `status.json` reports the remaining seconds, removing the directory cancels it, and running `dismiss.sh` silences everything currently ringing. Timers are deliberately in-memory and hub-local — a timer just *rings*, it never messages the agent, which is why this is a pure tool server rather than a channel.

The timers server has no audio path of its own. When a timer fires it calls the voice hub's token-gated HTTP endpoints (`/api/voice/announce`, `/api/voice/dismiss`, `/api/voice/satellites`), so ringing, dismissal (wake word or button on the satellite, or `dismiss.sh` from any channel), and satellite/room resolution all stay in one place — the hub. The `timers_prompt` MCP prompt teaches the `/timers` idiom and embeds the live satellite roster.

Clock-time alarms and reminders ride Home Assistant instead: the agent creates events on a dedicated `calendar.assistant_alarms` calendar and an HA automation bridges each firing to the same voice announce endpoint, with optional insistent repetition until acknowledged.

### Voice Satellites

Voice runs as another MCP channel server (`mcp-channel-voice`) plus dedicated hardware satellites. The hub is the Wyoming-protocol **client**: for every satellite configured with an address (`Satellites__<id>__Address`, e.g. `tcp://192.168.5.55:10800`) it dials out and keeps a persistent reconnecting connection. The satellite detects the wake word locally and streams mic audio to the hub; the hub segments the utterance (silence gating), gates it through speaker verification (an ONNX speaker-embedding model scored against profiles enrolled via `scripts/enroll-voice.sh` — non-enrolled audio like TV or guests is dropped before STT, and a conclusive match routes the speaker's identity into the message for per-person memory), optionally cleans noisy captures through the `tse-extractor` target-speech-extraction sidecar, transcribes via Lemonade STT (`lemonade`, OpenAI-compatible `/v1/audio/transcriptions`, Whisper on whisper.cpp), dispatches the transcript to the agent as a `channel/message`, and speaks the reply as it streams — segment-by-segment Lemonade Kokoro TTS audio (24 kHz PCM resampled in-hub to 22 050 Hz). After each reply an optional wake-free follow-up window opens (announced by a listening chime) so the conversation continues without repeating the wake word.

The hub also exposes token-gated HTTP endpoints (`/api/voice/announce`, `/api/voice/dismiss`, `/api/voice/satellites`) that `mcp-timers` and the Home Assistant alarm bridge call to ring announcements — including insistent repeat-until-acknowledged alarms — on any satellite (see [Timers & Alarms](#timers--alarms)).

```
┌─ Raspberry Pi / WSL dev host ──┐                     ┌─ mcp-channel-voice (hub) ──┐
│ nabu-satellite (static Rust)   │   Wyoming over TCP  │ silence gate → Whisper STT ┼──▶ channel/message ──▶ Agent
│ wake "ok nabu" → mic stream    ┼◀───(hub dials in)──▶│ Kokoro TTS ◀── send_reply  ┼◀── agent reply
│ arecord/aplay · cues · LED     │                     │ follow-up window + chime   │
└────────────────────────────────┘                     └────────────────────────────┘
```

#### nabu-satellite (`satellite/`)

The satellite is `nabu-satellite`, a from-scratch Rust replacement for the archived `wyoming-satellite` Python project: one fully static ~18 MB `aarch64-musl` binary with everything embedded (ONNX wake models, cue WAVs). Deployment needs no Python and no system packages beyond `alsa-utils`.

- **Wyoming server** — the satellite listens (default `0.0.0.0:10700`; the hub's configured `Address` must match the satellite's `--listen` port) and the hub dials in. A new hub connection supersedes any previous one, so a dead-peer TCP connection can't hold the exclusive mic device hostage.
- **On-device wake word** — the openWakeWord pipeline (melspectrogram → embedding → `ok_nabu` classifier) runs in-process via `tract`, validated against the Python implementation. A pre-roll ring buffer gives zero-lag turns: on wake only the detection gap is flushed (the wake word itself is never transcribed); a button press flushes the full ring instead, since speech may precede the press.
- **Audio** — mic capture via an `arecord` subprocess (16 kHz mono S16LE in 80 ms chunks); playback via `aplay` at a fixed 22 050 Hz (all hub-side audio — TTS, chime — is generated at that rate). Local cues bracket listening: the awake cue plays on turn start, the done cue when the transcript arrives and the mic re-arms.
- **Buttons & LED** — `--button-gpio`/`--button-evdev` starts a turn without the wake word (`--no-wake` for button-only operation; `--button-evdev` also covers a USB foot-switch). The reSpeaker XVF3800's 12-LED ring is driven automatically when the array is present, showing a distinct look per phase — Idle dark, Listening a blue ring with a green direction-of-arrival pointer, Thinking a breathing blue, Speaking solid blue; `--no-led` opts out. Missing hardware is never fatal.
- **Defaults** target a reSpeaker XVF3800 USB mic array paired with a HiFiBerry MiniAmp I2S speaker (LED ring on by default, no button); the ReSpeaker 2-Mic HAT path overrides the audio device and adds `--button-gpio 17` — its APA102 LEDs are no longer driven.

Build and deployment:

- `satellite/scripts/build-release.sh` — cross-compiles the fully static `aarch64-unknown-linux-musl` release via `cargo-zigbuild` (a CC shim translates the fp16 target feature for zig cc).
- `scripts/provision-satellite-rs.sh <user@host> [mic-device]` — builds, installs the binary and the `nabu-satellite.service` systemd unit on a Raspberry Pi, and — when the mic device is left at the default — auto-detects the USB audio card by name, applies a USB autosuspend-off udev rule, and (for the XVF3800 specifically) grants `plugdev` write access to the ring's USB device node.
- `scripts/wsl-satellite.sh` — runs a satellite on a WSL dev host through WSLg PulseAudio so the dockerized hub can dial `tcp://host.docker.internal:<port>`.

See `satellite/README.md` for build prerequisites, CLI flags, and dev-test commands.

### MCP Tool Servers

| Server            | Tools                                                                                                                   | Resources             | Purpose                                                                                 |
|-------------------|-------------------------------------------------------------------------------------------------------------------------|-----------------------|-----------------------------------------------------------------------------------------|
| **mcp-library**   | file_search, download_file, fs_glob, fs_read, fs_info, fs_move, fs_copy, fs_delete, fs_blob_read, fs_blob_write | `filesystem://media` | Search and download content via Jackett/qBittorrent, organize media files; dual-role — also a channel that pushes download-completion alerts to the originating conversation |
| **mcp-vault**     | fs_glob, fs_read, fs_search, fs_create, fs_edit, fs_move, fs_copy, fs_delete, fs_info, fs_blob_read, fs_blob_write    | `filesystem://vault`  | Manage a knowledge vault of markdown notes and text files                                |
| **mcp-sandbox**   | fs_glob, fs_read, fs_search, fs_create, fs_edit, fs_move, fs_delete, fs_copy, fs_info, fs_blob_read, fs_blob_write, fs_exec | `filesystem://sandbox` | Linux container for arbitrary bash/Python execution with a scratch + persistent home filesystem |
| **mcp-websearch** | web_search, web_browse, web_snapshot, web_action                                                                        |                       | Search the web and browse pages via Camoufox with accessibility tree snapshots            |
| **mcp-idealista** | property_search                                                                                                         |                       | Search real estate properties on Idealista (Spain, Italy, Portugal)                      |
| **mcp-homeassistant** | fs_glob, fs_read, fs_info, fs_search, fs_exec                                                                          | `filesystem://ha`     | Control a Home Assistant instance as a virtual filesystem (entities, areas, `.sh` action files); injects a slim setup index into the system prompt |
| **mcp-scheduling** | fs_glob, fs_read, fs_info, fs_search, fs_create, fs_edit, fs_move, fs_delete, fs_exec                                  | `filesystem://schedules` | Manage cron/one-shot scheduled agent tasks as a virtual filesystem; also runs as a channel that fires due schedules (see [Scheduled Tasks](#scheduled-tasks)) |
| **mcp-printer**   | fs_glob, fs_read, fs_info, fs_search, fs_create, fs_edit, fs_copy, fs_delete, fs_blob_read, fs_blob_write              | `filesystem://print-queue` | Print plain text/JPEG to a configured IPP/CUPS printer as a virtual filesystem — copy/create to submit, remove to cancel (see [Printing](#printing)) |
| **mcp-timers**    | fs_glob, fs_read, fs_info, fs_search, fs_create, fs_delete, fs_exec                                                    | `filesystem://timers`      | Hub-local countdown timers as a virtual filesystem — create to arm, read status.json for time left, remove to cancel, exec dismiss.sh to stop ringing; rings on the voice satellites via the voice hub |
| **outpost**       | fs_glob, fs_read, fs_search, fs_create, fs_edit, fs_move, fs_delete, fs_copy, fs_info, fs_blob_read, fs_blob_write, fs_move_out_check, (fs_exec with `--exec`) | `filesystem://<name>` | A real machine's own filesystem, served by a single-file binary somebody copies onto it and runs. **Not a container**: no Dockerfile, no compose service, no appsettings — it takes flags, registers itself with the hub and expires on its own when the machine sleeps (see [Outposts](#outposts)) |

### MCP Channel Servers

| Server                    | Purpose                                                                                    |
|---------------------------|--------------------------------------------------------------------------------------------|
| **mcp-channel-signalr**   | WebChat transport — hosts SignalR hub, manages streams/sessions/approvals, push notifications |
| **mcp-channel-telegram**  | Telegram transport — multi-bot polling (one per agent), inline keyboard approvals            |
| **mcp-channel-servicebus**| Azure Service Bus transport — queue processor, auto-approval, response sender                |
| **mcp-channel-voice**     | Voice transport — Wyoming hub that dials hardware satellites, segments utterances, gates speakers (verification + optional TSE cleanup), Lemonade STT/TTS (OpenAI-compatible, `lemonade`), manages follow-up windows and announcements (see [Voice Satellites](#voice-satellites)) |
| **mcp-scheduling**        | Scheduling transport — fires due cron/one-shot schedules as channel messages; also exposes `filesystem://schedules` for managing them |

### Agents

| Agent     | MCP Servers                                         | Features                                    | Purpose                                                                   |
|-----------|-----------------------------------------------------|---------------------------------------------|---------------------------------------------------------------------------|
| **Jack**  | mcp-library, mcp-websearch                          | filesystem (glob, move)                     | Media acquisition and library management ("Captain Jack" pirate persona)  |
| **Jonas** | mcp-vault, mcp-sandbox, mcp-websearch, mcp-idealista, mcp-homeassistant, mcp-scheduling, mcp-printer, mcp-timers | filesystem, subagents, memory   | Knowledge base management ("Scribe" persona) with subagent delegation, sandbox execution, scheduled tasks, Home Assistant control, printing, and countdown timers |
| **Nabu**  | same as Jonas                                       | filesystem, subagents, memory               | Voice-optimized assistant — brief, formatting-free spoken replies delivered through the voice satellites (also serves as the `voice` channel) |

### Multi-Agent Configuration

Agents are defined as configuration data, each with:
- Custom LLM model selection
- Specific MCP server endpoints
- Tool whitelist patterns (e.g., `mcp:mcp-library:*`, `domain:subagents:*`)
- Custom system instructions
- Enabled features (e.g., `filesystem`, `subagents`, `memory`)

#### Subagents

Agents with the `subagents` feature enabled can delegate work to ephemeral subagents via the `run_subagent` tool. Subagents are configured per-agent in `appsettings.json` under the `subAgents` array, each with its own model, MCP server endpoints, and execution timeout. Subagents use ephemeral state (no Redis persistence) and cannot spawn further subagents.

Agent routing:
- **Telegram**: Each bot token maps to one agent (configured per channel)
- **WebChat**: User selects agent from available list in the UI
- **Service Bus**: Agent specified in message `agentId` field (falls back to default)

## Projects

| Project                  | Description                                                     |
|--------------------------|-----------------------------------------------------------------|
| `Agent`                  | Composition root, connects to channel and tool MCP servers      |
| `Domain`                 | Core domain logic, agent contracts, channel protocol DTOs       |
| `Infrastructure`         | External service clients (MCP, OpenRouter, push notifications)  |
| `McpServerLibrary`       | MCP server for torrent search, downloads, and file organization |
| `McpServerVault`          | MCP server for text/markdown file inspection and editing        |
| `McpServerSandbox`       | MCP server exposing a Linux sandbox container with `fs_exec`    |
| `McpServerWebSearch`     | MCP server for web search and browsing via Camoufox             |
| `McpServerIdealista`     | MCP server for Idealista real estate property search            |
| `McpServerHomeAssistant` | MCP server exposing Home Assistant as `filesystem://ha`         |
| `McpServerScheduling`    | MCP server for scheduled tasks (`filesystem://schedules` + channel) |
| `McpServerPrinter`       | MCP server exposing a print queue as `filesystem://print-queue` (IPP/CUPS) |
| `McpServerTimers`        | MCP server exposing countdown timers as `filesystem://timers`; rings via the voice hub's HTTP endpoints |
| `McpServerOutpost`       | MCP server for a real machine's own filesystem — self-contained single-file `linux-x64` binary, self-registering, the one server with no Dockerfile and no compose service |
| `McpChannelSignalR`      | MCP channel server for WebChat (SignalR hub, streaming, push)   |
| `McpChannelTelegram`     | MCP channel server for Telegram (multi-bot, approvals)          |
| `McpChannelServiceBus`   | MCP channel server for Azure Service Bus (queues)               |
| `McpChannelVoice`        | MCP channel server for voice satellites (Wyoming hub, Lemonade STT/TTS) |
| `satellite`              | `nabu-satellite` — standalone Rust crate: static Wyoming satellite binary for Raspberry Pi (wake word, audio I/O, button, LED) |
| `WebChat`                | Blazor WebAssembly host server for browser-based chat           |
| `WebChat.Client`         | Blazor WebAssembly client with chat UI and SignalR integration  |
| `Observability`          | Metrics collector, REST API, SignalR hub — serves the Dashboard |
| `Dashboard.Client`       | Blazor WebAssembly observability dashboard (PWA)                |
| `DockerCompose`          | Docker Compose configuration for the full stack                 |
| `Tests`                  | Unit, integration, and E2E tests                                |

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker](https://www.docker.com/) and Docker Compose
- [OpenRouter API Key](https://openrouter.ai/)
- [Telegram Bot Token](https://core.telegram.org/bots#creating-a-new-bot) (one per agent)

## Getting Started

### 1. Clone and Configure

```bash
git clone https://github.com/Dethon/Ziggurat.git
cd Ziggurat
```

### 2. Set Environment Variables

Edit the environment file:

```bash
cd DockerCompose
# Edit .env with your configuration
```

Required variables:

```env
REPOSITORY_PATH=..
DATA_PATH=./volumes/data
VAULT_PATH=./volumes/vault
PUID=1000
PGID=1000
OPENROUTER__APIKEY=your_openrouter_api_key
OPENROUTER__APIURL=https://openrouter.ai/api/v1/
BRAVE__APIKEY=your_brave_search_api_key
IDEALISTA__APIKEY=your_idealista_api_key
IDEALISTA__APISECRET=your_idealista_api_secret
HOMEASSISTANT__TOKEN=your_home_assistant_long_lived_access_token
JACKETT__APIKEY=your_jackett_api_key
QBITTORRENT__USERNAME=admin
QBITTORRENT__PASSWORD=your_password

# Agent definitions (array)
AGENTS__0__ID=jack
AGENTS__0__NAME=Jack
AGENTS__0__MODEL=openai/gpt-5.6-luna
AGENTS__0__MCPSERVERENDPOINTS__0=http://mcp-library:8080/mcp
AGENTS__0__MCPSERVERENDPOINTS__1=http://mcp-websearch:8080/mcp
AGENTS__0__ENABLEDFEATURES__0=filesystem.glob
AGENTS__0__ENABLEDFEATURES__1=filesystem.move
AGENTS__0__WHITELISTPATTERNS__0=domain:filesystem:*
AGENTS__0__WHITELISTPATTERNS__1=mcp:mcp-library:*
AGENTS__0__WHITELISTPATTERNS__2=mcp:mcp-websearch:*
AGENTS__0__CUSTOMINSTRUCTIONS=You are Jack, a media library assistant...

# Subagent definitions (per-agent, optional)
SUBAGENTS__0__ID=jonas-worker
SUBAGENTS__0__NAME=Jonas Worker
SUBAGENTS__0__DESCRIPTION=A worker subagent with the same toolset as Jonas
SUBAGENTS__0__MODEL=openai/gpt-5.6-luna
SUBAGENTS__0__MCPSERVERENDPOINTS__0=http://mcp-vault:8080/mcp
SUBAGENTS__0__MAXEXECUTIONSECONDS=600

# Channel endpoints (agent connects to these)
CHANNELENDPOINTS__0__CHANNELID=signalr
CHANNELENDPOINTS__0__ENDPOINT=http://mcp-channel-signalr:8080/mcp
CHANNELENDPOINTS__1__CHANNELID=telegram
CHANNELENDPOINTS__1__ENDPOINT=http://mcp-channel-telegram:8080/mcp
CHANNELENDPOINTS__2__CHANNELID=servicebus
CHANNELENDPOINTS__2__ENDPOINT=http://mcp-channel-servicebus:8080/mcp
CHANNELENDPOINTS__3__CHANNELID=scheduling
CHANNELENDPOINTS__3__ENDPOINT=http://mcp-scheduling:8080/mcp
CHANNELENDPOINTS__4__CHANNELID=voice
CHANNELENDPOINTS__4__ENDPOINT=http://mcp-channel-voice:8080/mcp
CHANNELENDPOINTS__4__ATTACHONLY=true
CHANNELENDPOINTS__5__CHANNELID=library
CHANNELENDPOINTS__5__ENDPOINT=http://mcp-library:8080/mcp

# Telegram channel (one bot per agent)
BOTS__0__AGENTID=jack
BOTS__0__BOTTOKEN=your_telegram_bot_token_for_jack
ALLOWEDUSERNAMES__0=your_telegram_username

# SignalR channel
AGENTS__0__ID=jack
AGENTS__0__NAME=Jack
AGENTS__0__DESCRIPTION=General assistant

# Service Bus channel (optional)
SERVICEBUSCONNECTIONSTRING=Endpoint=sb://yournamespace.servicebus.windows.net/;SharedAccessKeyName=...
```

### 3. Run with Docker Compose

```bash
cd DockerCompose

# Linux / WSL
docker compose -f docker-compose.yml -f docker-compose.override.linux.yml -p jackbot up -d --build

# Windows
docker compose -f docker-compose.yml -f docker-compose.override.windows.yml -p jackbot up -d --build
```

## Services & Ports

| Service                  | Port | Description                    |
|--------------------------|------|--------------------------------|
| WebChat                  | 5001 | Browser-based chat interface   |
| Dashboard                | 5003 | Observability dashboard (PWA)  |
| Redis                    | 6379 | Conversation state persistence |
| qBittorrent              | 8001 | Torrent client WebUI           |
| FileBrowser              | 8002 | File management WebUI          |
| Jackett                  | 8003 | Torrent indexer proxy          |
| MCP Library              | 6001 | Library MCP server             |
| MCP Vault                | 6002 | Document vault MCP server      |
| MCP WebSearch            | 6003 | Web search MCP server          |
| MCP Sandbox              | 6004 | Linux sandbox MCP server       |
| MCP Idealista            | 6005 | Idealista property MCP server  |
| MCP HomeAssistant        | 6006 | Home Assistant MCP server      |
| Home Assistant           | 8123 | Home Assistant web UI/API      |
| MCP Channel SignalR      | 6010 | WebChat channel server         |
| MCP Channel Telegram     | 6011 | Telegram channel server        |
| MCP Channel ServiceBus   | 6012 | ServiceBus channel server      |
| MCP Scheduling           | 6013 | Scheduling channel + filesystem server |
| MCP Printer              | 6014 | Print queue MCP server         |
| MCP Channel Voice        | 6015 | Voice channel server (Wyoming satellite hub) |
| MCP Timers               | 6016 | Countdown timers MCP server    |
| Lemonade                 | 13305| STT/TTS server (OpenAI-compatible) |
| TSE Extractor            | 9098 | Target-speech-extraction service |
| Camoufox                 | 9377 | Anti-detect browser (WebSocket)|
| Caddy                    | 443  | TLS entry point (WebChat, SignalR hubs, dashboard) |

## Usage

### WebChat Interface

Access the browser-based chat at `http://localhost:5001` after starting the Docker Compose stack. In production, connect through Caddy (port 443) which routes `/hubs/*` to the SignalR channel server.

Features:
- **Real-time streaming** - Messages stream as they're generated with automatic reconnection
- **Topic management** - Organize conversations into topics with server-side persistence
- **Multi-agent selection** - Switch between available agents from the UI
- **User identity selection** - Switch between configured user identities with persistent avatars
- **Stream resumption** - Reconnects automatically and resumes from where you left off
- **Push notifications** - Browser push notifications when responses complete (VAPID-based)

#### User Identity Configuration

WebChat supports multiple user identities configured via `WebChat/appsettings.json` or environment variables:

**appsettings.json:**
```json
{
  "Users": [
    { "Id": "Alice", "AvatarUrl": "avatars/alice.png" },
    { "Id": "Bob", "AvatarUrl": "avatars/bob.png" }
  ]
}
```

**Environment variables (Docker Compose):**
```env
USERS__0__ID=Alice
USERS__0__AVATARURL=avatars/alice.png
USERS__1__ID=Bob
USERS__1__AVATARURL=avatars/bob.png
```

Place avatar images in `WebChat.Client/wwwroot/avatars/`. Selected identity persists in browser local storage.

### Observability Dashboard

Access at `http://localhost:5003/dashboard/` (direct) or `https://yourdomain/dashboard/` (via Caddy). Installable as a PWA.

The dashboard provides operational visibility into agent behavior:

- **Overview** — KPI cards (tokens, cost, tool calls, errors), service health grid, recent activity feed
- **Tokens** — Token usage time-series, cost breakdown, per-user and per-model tables
- **Tools** — Tool call frequency, success/failure rates, average duration
- **Errors** — Error list with type, service, and message details
- **Latency** — Per-turn latency trends with percentile breakdowns and a recent-turns table
- **Voice** — Voice pipeline analytics (turn-span decomposition, STT/TTS latency percentiles) and recent voice events
- **Schedules** — Schedule execution history with duration and success/failure status
- **Memory** — Memory extraction, recall, and dreaming analytics

Data flows via Redis Pub/Sub: all services emit metric events through `IMetricsPublisher`, the Observability collector aggregates them into Redis, and the dashboard reads via REST API with live updates via SignalR.

### Telegram Interface

Each agent gets its own Telegram bot. Configure bot tokens in the Telegram channel's environment:

```env
BOTS__0__AGENTID=jack
BOTS__0__BOTTOKEN=your_jack_bot_token
BOTS__1__AGENTID=jonas
BOTS__1__BOTTOKEN=your_jonas_bot_token
ALLOWEDUSERNAMES__0=your_telegram_username
```

### Service Bus Interface

Azure Service Bus integration for external system connectivity. The channel listens for prompts on a queue and writes responses to another queue.

**Prompt Message Format** (sent to prompt queue):
```json
{
  "correlationId": "unique-request-id",
  "agentId": "jack",
  "prompt": "Your question or command here",
  "sender": "external-system-id"
}
```

**Response Message Format** (written to response queue):
```json
{
  "correlationId": "unique-request-id",
  "content": "Agent's response text"
}
```

### Tool Approval

When the agent wants to execute a tool:
- **Approve** - Allow the tool to run once
- **Always** - Auto-approve this tool for the session
- **Reject** - Block the tool execution

Configure permanent auto-approvals using glob patterns:

```json
{
  "whitelistPatterns": [
    "mcp:mcp-library:*",
    "domain:filesystem:*",
    "domain:memory:*"
  ]
}
```

Pattern format: `mcp:<server>:<tool>` or `domain:<feature>:<tool>` with `*` wildcard support.

### Chat Commands

| Command   | Description                                           |
|-----------|-------------------------------------------------------|
| `/cancel` | Cancel current operation (keeps conversation history) |
| `/clear`  | Clear conversation and wipe thread history from Redis |

## Persistence

The agent uses Redis to persist conversation history and memory across restarts:

- **Chat History** - All messages are stored with a 30-day expiry
- **Thread State** - Each chat thread is identified by `agent-key:{agentId}:{conversationId}`
- **Download Tracking** - Completion alerts arrive automatically in the originating conversation (routing snapshots stored in Redis survive restarts); live status is readable anytime via `/media/downloads/<id>/status.json`
- **Memory Storage** - Proactively extracted memories from windowed conversation context, stored in Redis with vector search for semantic recall; periodic dreaming consolidates and prunes
- **Schedules** - Cron and one-shot schedule definitions stored in Redis, polled by the scheduling server's dispatcher; one-shot schedules are auto-deleted after firing
- **Push Subscriptions** - Browser push notification subscriptions stored in Redis per space
- **Metrics** - Token usage, tool calls, errors, and schedule executions stored as Redis sorted sets and hashes with 30-day TTL
- **Service Health** - Heartbeat-based health tracking with 60-second TTL keys in Redis

## License

[CC BY-NC-ND 4.0](LICENSE) - Attribution-NonCommercial-NoDerivatives 4.0 International
