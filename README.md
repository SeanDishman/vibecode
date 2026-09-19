# VibeCode

**Run a whole team of AI coding agents on one project, from one window.** VibeCode wraps the terminal agents you
already use - Claude Code, OpenAI Codex, Kimi Code, and Grok - in a native WPF interface, then adds the thing a
terminal can't give you: **multiple agents working the same codebase with shared memory, a manager that assigns
them lanes, a broadcast channel that interrupts all of them at once, and per-agent sub-agent swarms.**

For Claude, Codex, Kimi, and Grok, each chat launches an installed CLI as a child process and speaks its streaming
protocol. VibeCode adds account switching, session history, MCP configuration, and permission controls around
those runtimes. A separate **GLM provider** connects through an API account you configure in the app.

> [!WARNING]
> **VibeCode is a work in progress.** It is under active development - expect rough edges, changing behavior, and
> features that are still landing. **Found a bug? [Open an issue](../../issues).** Crashes, broken tool cards, a
> provider that won't connect, layout weirdness - all of it is worth reporting. Include what you did, what you
> expected, what happened, and which provider you were on. Bug reports are the fastest way to make this better.

![Two Grok agents working the same project in a VibeCode bridge](assets/screenshots/bridge-hero.png)

---

## Table of contents

- [What it is](#what-it-is)
- [Bridges - many agents, one shared memory](#bridges---many-agents-one-shared-memory)
- [Announce - interrupt every agent at once](#announce---interrupt-every-agent-at-once)
- [Bridge manager - one agent assigns the work](#bridge-manager---one-agent-assigns-the-work)
- [Agent swarms - provider-native sub-agents](#agent-swarms---provider-native-sub-agents)
- [Queues and the orchestrator wall](#queues-and-the-orchestrator-wall)
- [Second Brain - memory across chats](#second-brain---memory-across-chats)
- [MCP servers - one catalog, every CLI](#mcp-servers---one-catalog-every-cli)
- [Usage and cost tracking](#usage-and-cost-tracking)
- [Android companion](#android-companion)
- [Everything else](#everything-else)
- [Requirements](#requirements)
- [Quick start](#quick-start)
- [Setting up the coding agents](#setting-up-the-coding-agents)
- [Configuration](#configuration)
- [Environment variables](#environment-variables)
- [Project layout](#project-layout)
- [Building a release](#building-a-release)
- [Troubleshooting](#troubleshooting)

---

## What it is

Agentic coding CLIs are excellent but live in a terminal: output scrolls away, diffs are hard to read, running two
agents on one project means juggling windows, and switching accounts means logging out and back in.

VibeCode keeps the CLI as the engine and replaces only the surface:

- **The CLI is the engine.** Claude, Codex, Kimi, and Grok chats run their respective CLI in your project folder.
  VibeCode translates its stream to UI and your input back to the protocol. GLM uses its own API adapter.
- **Your setup is supported.** Provider configuration, MCP servers, permission modes, and session history stay
  part of the workflow. Managed accounts use separate credential storage so you can switch accounts in the app.
- **Activity comes from the provider.** Tool calls, diffs, usage, and rate limits reflect provider reports.
  Live token estimates are marked with `~` and reconciled when reported totals arrive.

With **Run in background** enabled, closing the window keeps ongoing work running in the system tray. Use
**Quit** to end the app, or disable background running in Settings. The app binds its CLI children to Win32 Job
Objects so exiting or crashing cleans up the processes it owns. Independently launched terminal sessions keep
their own lifetime.

## Bridges - many agents, one shared memory

![A bridge running two agents side by side, each aware of the other](assets/screenshots/bridge.png)

A **Bridge** puts multiple independent agents on the *same project folder* at the same time, side by side in one
grid. Each pane is a full root CLI session with its own composer, provider, model, reasoning effort, and
permission mode - you can run Claude, Codex, and Grok together on one codebase.

The hard part of multi-agent coding is that agents don't know what the others are doing, so they collide, redo
work, and overwrite each other. Bridges solve that with **one shared memory file** the agents themselves maintain:
`.vibecode-bridge.md` in the project root.

- **Area-claims board** (`## Active`) - every agent keeps a short block naming what it's working on. Before doing
  anything else, a joining agent reads the board and picks an area nobody has claimed.
- **Live-activity board** (`## Live activity`) - with real-time sharing on, each agent also keeps a one-block
  snapshot of the file(s) it's touching *right now* and what it's changing there. Peers check it before editing a
  file, so two agents don't land in the same file. It's rewritten in place at checkpoints (start, switch, finish a
  file), never appended, which keeps the awareness cheap in tokens.
- **Real-time sharing off** falls back to high-level coordination: agents stay out of each other's areas without
  tracking line-by-line activity.

Agents can also send **peer messages** and look up **peer conversations** to hand off work or ask for context.
The shared file remains the durable coordination board; messaging and conversation lookup complement it.

Bridges hold **up to 17 agents** (default limit 9), and you can mix providers freely - add another agent from the
bridge header and pick whichever CLI suits the lane:

<img src="assets/screenshots/add-agent.png" alt="Adding a Claude, Codex, Kimi, or Grok agent to a bridge" width="280">

A bridge keeps running in the background when you navigate away, is restored after a crash from a recovery
snapshot, and auto-closes after an idle timeout - never mid-task. When an agent leaves, its claimed area is
released and the remaining agents are told.

Bridge behavior is configurable in Settings - real-time sharing, the agent ceiling, dual-monitor layout, and
per-agent completion notifications:

<img src="assets/screenshots/settings.png" alt="VibeCode settings: notifications, dual-monitor bridge, real-time sharing, max agents per bridge" width="560">

## Announce - interrupt every agent at once

<!-- ![The announce composer broadcasting to every agent](assets/screenshots/announce.png) -->

Sometimes you need every agent to stop and hear the same thing: a change of direction, a constraint you forgot, a
"stop touching the auth module."

**Announce** does exactly that. Type one message in the bridge header, hit send, and VibeCode **interrupts every
agent on the bridge** and delivers that single message into all of their sessions at once. No repeating yourself
per pane, no agent continuing on stale instructions.

## Bridge manager - one agent assigns the work

<!-- ![The crowned manager dispatching lanes to its workers](assets/screenshots/manager.png) -->

Crown any pane as the bridge's **manager** (👑) and the bridge becomes hierarchical: you stop directing agents
individually and run the whole project through one of them.

The manager is the brain. You talk to it; it decomposes the project into **non-overlapping lanes** (disjoint
files and areas) and assigns them to the other agents, which become its workers.

- **Dispatch** - the manager assigns work by emitting a block in its reply:

  ```
  @@DISPATCH agent=3
  Refactor the settings dialog. Own Services/AppSettings.cs and SettingsWindow.xaml.
  Do not touch the composer or Themes/.
  @@END
  ```

  VibeCode extracts each block when the reply finishes and delivers it **into that worker's session**. Workers
  never see the rest of the manager's reply, and `agent=all` broadcasts one order to everyone. Dispatching to a
  busy worker is fine - it queues and arrives when that worker's current turn ends, so you can steer workers
  without interrupting them.
- **Reports flow back automatically.** A worker ends each task with a short factual report, and the tail of its
  reply is relayed to the manager for you.
- **The manager reacts to events.** VibeCode messages it a `👑 [MANAGER UPDATE]` whenever a worker finishes,
  errors, joins, or leaves - so it verifies the work, updates its plan, and immediately dispatches that freed
  worker its next lane. Idle workers get refilled without you saying "continue."
- **The plan is durable.** The manager maintains a `## Manager plan` section in `.vibecode-bridge.md`, which is
  its memory if anything restarts.
- **You stay in the loop.** Talk to the manager at any time while workers run; it folds your input into the plan.
  When every lane is done it dispatches a final verification pass, tells you, and stops.

Crowning is reversible, the crown follows the conversation if you move it, and if the manager leaves the bridge
the remaining agents are told to go back to coordinating as equals.

## Agent swarms - provider-native sub-agents

<!-- ![An agent fanning a task out to child workers](assets/screenshots/swarm.png) -->

Bridges give you parallel *root* sessions. **Swarms** give one agent parallel *children*: VibeCode surfaces the
CLI's own sub-agent capability - Claude Agent subagents, Codex collaboration subagents, or Grok subagents - so a
single chat can fan a task out across several workers and integrate the results itself.

- **Opt-in per turn.** Making the capability available doesn't make every prompt fan out; a swarm directive is
  attached only to a turn you explicitly mark as a swarm request. Ordinary prompts cost what you expect.
- **Bounded.** You choose the ceiling - default 6 child workers, configurable 2-16, with a hard cap enforced even
  if `settings.json` is hand-edited. The agent is told to pick the *smallest useful* swarm, and using none is a
  valid answer.
- **Flat by design.** Child workers may not spawn their own children, so a swarm can't fork-bomb your token
  budget. Workers get disjoint scopes; the parent waits for all of them, verifies, then integrates.
- **Composes with Bridges, carefully.** Swarms inside a bridge pane are a *separate* opt-in, since bridge peers
  are already parallel roots. Bridge peers are never counted as, messaged as, or commandeered as swarm workers.

Supported for Claude, Codex, and Grok.

## Queues and the orchestrator wall

Queue follow-up prompts while an agent is working, or use a longer task queue to keep a sequence of work visible
in the chat. Bridge supervision tracks ongoing work and worker updates alongside the manager's plan.

**Demon Mode** opens an orchestrator wall with a selectable team of **4-17 root sessions**: one orchestrator and
up to 16 workers. You direct the orchestrator from its main pane and follow worker progress in the surrounding
grid. It uses the bridge's session and dispatch machinery, with the orchestrator responsible for planning and
assigning lanes.

## Second Brain - memory across chats

Second Brain connects chats and bridge agents to an **agentmemory** service for durable memory, automatic recall,
and remembering useful decisions and corrections. Search memories or explore their connections in the memory
graph. Per-chat controls let you exclude a conversation from capture and recall.

The default endpoint is a local service at `http://127.0.0.1:3111`. Capture and recall require a running compatible
service. The memory map has an on/off control; connection and automatic recall/remembering options are stored as
`AgentMemoryEndpoint`, `AgentMemoryAutoRecall`, and `AgentMemoryAutoRemember` in `settings.json`. Set
`AGENTMEMORY_SECRET` when your service requires authentication.

## MCP servers - one catalog, every CLI

<!-- ![The MCP server catalog and config assistant](assets/screenshots/mcp.png) -->

Define an MCP server **once** in VibeCode and use it across providers. VibeCode keeps a canonical catalog and
translates each entry at the process boundary into whatever that CLI expects - Claude JSON, Codex TOML overrides,
or ACP for Kimi and Grok.

- **All three transports**: local `stdio`, remote Streamable HTTP, and legacy SSE.
- **Validated before it can break a session** - bad definitions are caught with clear errors up front.
- **Guided config assistant** - describe the server you want in plain English and a short isolated agent turn
  drafts the definition (researching official docs when the package is ambiguous). Nothing is executed or saved
  until it passes validation and you approve it.
- **Not a proxy.** Each CLI remains the MCP client and owns its own tool approvals; VibeCode only configures.

## Usage and cost tracking

VibeCode tracks what you spend across every provider and model in one dashboard: estimated spend, tokens in and
out, cache hit rate, and turn count over Today / 7 days / 30 days / All time, with a per-model cost and token
breakdown.

![Usage dashboard: estimated spend, tokens, cache rate, and per-model cost breakdown](assets/screenshots/usage.png)

Spend is estimated from each model's public pricing, and the cache-served percentage shows how much of your token
volume came back from prompt caching rather than being billed fresh. It is a rough guide, not an invoice.

Chat headers also show elapsed time, token totals, and rolling **tok/s** and **tok/min**. When a provider reports
usage in a large batch, the rate spreads those tokens over the reporting interval and averages over up to the
last 60 seconds. A delayed batch therefore reflects the time spent producing it instead of appearing as a
one-second spike. Rates count input, cached input, and output tokens. A `~` marks provisional streaming estimates
until reported usage arrives.

## Android companion

The Android companion connects to your running desktop to follow chats, send prompts, and handle supported
approvals and session controls. Enable **Phone** in Settings, start the desktop connection, and pair the device.
Connections use authentication and pinned TLS certificates; the desktop must remain running and reachable.

The desktop can generate a paired installer from an **unconfigured Android template**. Build that template from
source with `scripts/Build-Mobile.ps1`, or include it when publishing with `scripts/Publish.ps1 -IncludeMobile`.
Pairing credentials and the signing identity are generated on the desktop. See the
[Android guide](mobile/README.md) for build, signing, and enrollment details.

## Everything else

**Chat**

- Markdown rendering with selectable text, code blocks, and a full-file syntax-highlighted diff viewer
- Editable code viewer, file navigation, compact edit cards, and workspace checkpoints for undoing a turn
- Collapsible tool-call cards with per-call status (running / done / error)
- Thinking blocks, an artifacts panel, and a live task list that updates as the agent works:

  <img src="assets/screenshots/todos.png" alt="Live task list showing an agent's progress through its plan" width="520">
- Permission-mode, model, and reasoning-effort pickers in the composer; all persist across restarts
- Provider-supported speed tiers and model catalogs, with fallback choices while a runtime is starting
- Session catalog: resume or fork any previous session; prompt and recent-directory history

**Accounts**

- Switch between multiple logins per provider from the sidebar without logging out and back in
- Managed provider accounts keep their own credential records; Codex accounts use separate account homes
- API-key accounts for supported providers, with keys protected for the current Windows user using DPAPI

**Extras**

- Rate-limit and usage display (session / week) with cost estimates
- Local Whisper speech-to-text dictation with optional Vulkan acceleration and CPU fallback
- Optional Groq-hosted dictation using your own API key; enabling it uploads the recorded clip to Groq
- Embedded browser panel and browser tools, animated backgrounds with an adjustable scrim, and theme choices
- Optional Spotify playback controls, weather and radar, and built-in games
- Completion notifications and continued work in the system tray
- Dual-monitor support: run the bridge on a second display

## Requirements

| | |
|---|---|
| OS | Windows 10 or 11 (WPF; Windows-only by design) |
| SDK | [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) to build; target framework `net8.0-windows` |
| Runtime | .NET 8 Desktop Runtime for a framework-dependent run; a self-contained release includes it |
| Agents | A supported CLI installed and signed in, or your own GLM API account (see below) |
| Browser | Microsoft Edge WebView2 Runtime for embedded web surfaces; installed Chrome for Chrome-based tools |
| Android builds | A compatible JDK, Android SDK platform 36, and the build tools selected by Gradle; optional for desktop-only builds |

## Quick start

```bash
git clone <your-fork-url> vibecode
```

```bash
cd vibecode && dotnet build VibeCode.Desktop/VibeCode.Desktop.csproj
```

```bash
dotnet run --project VibeCode.Desktop/VibeCode.Desktop.csproj
```

On first launch, pick a project folder, choose a provider, and start a chat. If the provider's CLI is missing or
signed out, VibeCode shows a banner explaining which step is needed - that banner reflects the CLI's real state.

## Setting up the coding agents

VibeCode discovers each CLI on `PATH` (plus the usual per-tool install locations). Install and log in with the
tool's own instructions, then confirm it runs from a terminal before using it here:

| Provider | Executable | Auth |
|---|---|---|
| Claude Code | `claude.exe` | `claude` then `/login` |
| OpenAI Codex | `codex.exe` | `codex login` |
| Kimi Code | `kimi.exe` / `kimi.cmd` | Kimi Code sign-in |
| Grok | `grok.exe` / `grok.cmd` | Grok CLI sign-in |
| GLM | Built-in API adapter | Add your own API-key account in VibeCode |

If a CLI is installed somewhere unusual, point VibeCode straight at it with `VIBECODE_CLAUDE_PATH`,
`VIBECODE_CODEX_PATH`, `VIBECODE_KIMI_PATH`, or `VIBECODE_GROK_PATH`. Install the CLI for every CLI-backed provider
you want to use; those runtimes are not bundled. Available models, effort choices, speed tiers, and usage limits
depend on the installed runtime and your account.

## Configuration

Settings persist to:

```
%APPDATA%\VibeCode\settings.json
```

That file holds your window placement, chosen model and effort, hidden projects, imported backgrounds, extension
options, and provider preferences. Provider authentication and encrypted API-key records are stored separately.
Settings and MCP definitions can still contain details you entered, so keep runtime data, account files, signing
keys, and generated enrollment files out of source control. Set `VIBECODE_DATA_DIR` to use an isolated app folder.

## Environment variables

All are optional.

| Variable | Purpose |
|---|---|
| `VIBECODE_DATA_DIR` | Redirect all VibeCode state to an isolated folder (useful for testing) |
| `VIBECODE_CLAUDE_PATH` / `VIBECODE_CODEX_PATH` / `VIBECODE_KIMI_PATH` / `VIBECODE_GROK_PATH` | Absolute path to a provider CLI |
| `CLAUDE_CONFIG_DIR` / `CODEX_HOME` / `KIMI_CODE_HOME` / `GROK_HOME` | Provider configuration locations; managed accounts may select their own account home |
| `AGENTMEMORY_SECRET` | Authentication secret for your configured Second Brain service |
| `JAVA_HOME` / `ANDROID_HOME` | JDK and Android SDK locations when building the mobile companion |
| `VIBECODE_BRIDGE_TIMEOUT_SECONDS` | Idle timeout before a background bridge disposes its peers |
| `VIBECODE_DISABLE_BROWSER_BRIDGE` | Disable the embedded browser bridge |
| `VIBECODE_OPEN_SETTINGS` | Open the settings window at startup |
| `VIBECODE_HIDDEN` | Launch off-screen for automated UI testing |

## Project layout

```
assets/                     App icon, logo, and README screenshots
VibeCode.Desktop/
  Protocol/                 Provider session drivers behind ICodingSession
    ClaudeSession.cs          Claude Code stream-json protocol + process/job lifecycle
    CodexSession.cs           Codex app-server protocol
    KimiSession.cs            ACP protocol (shared by Kimi and Grok)
    GrokSession.cs            Grok facade over the ACP session
    GlmSession.cs             GLM API session
  Services/                 Accounts, memory, phone, usage, MCP, speech, sessions, pricing
  UI/                       ViewModels, converters, diff/syntax rendering, extra windows
  Themes/                   Dark.xaml (design tokens) and Cli.xaml
  Assets/                   Background art and bundled application resources
  MainWindow.xaml(.cs)      Shell: sidebar, chat, composer, bridge overlay
mobile/VibeCodeMobile/      Android companion source and Gradle wrapper
tests/VibeCode.PublicTests/ Provider, token-rate, and WPF layout regression checks
scripts/                   Build, mobile-template validation, and publish helpers
```

Adding a provider means implementing `ICodingSession` and registering it - the UI is protocol-agnostic.

## Building a release

Build a self-contained single-file Windows executable from the repository root:

```powershell
powershell -NoProfile -File scripts/Publish.ps1
```

The output lands in `artifacts/windows-x64/`. Native Whisper libraries are extracted at runtime so dictation
works from the single file. Add `-IncludeMobile` to compile and embed the Android template:

```powershell
powershell -NoProfile -File scripts/Publish.ps1 -IncludeMobile
```

The publish script validates any included mobile template before embedding it. Templates contain no configured
desktop, pairing secret, or signing identity. Android build prerequisites are only required when building one.

### Verification

Run the local regression checks without provider accounts or live coding requests:

```powershell
dotnet run --project tests/VibeCode.PublicTests/VibeCode.PublicTests.csproj -c Release
powershell -NoProfile -File tests/Test-MobileTemplate.ps1
```

They cover provider catalogs, reasoning and speed options, a **50,000-token burst simulation**, delayed usage
reports, estimate reconciliation, independent chat panes, idle timers, header layouts, and enrollment validation.
Building the mobile template first also enables the APK personalization check. The GitHub Actions workflow builds,
runs these checks, and publishes a Windows artifact for each push and pull request.

## Troubleshooting

**"Could not find the … CLI"** - the executable is not on `PATH`. Verify it runs in a terminal, then set the
matching `VIBECODE_*_PATH` variable.

**A sign-in banner appears even though the terminal works** - check which account is selected in VibeCode and
sign in again through its account controls. Managed accounts can have a different login from your terminal.

**Build fails with a file-in-use error** - VibeCode is still running and holding its own `.exe`. Choose **Quit**
from the tray menu (closing the window can leave it running), then rebuild.

**Bridge peers keep running after you navigate away** - that is intentional; they run in the background. Close the
bridge explicitly, or let the idle timeout dispose it.

**The token counter updates in large bursts** - some providers report usage only at checkpoints or at the end of
a request. The rate display averages those reports over elapsed time. `~` values are live estimates that are
reconciled with reported totals when available.

**Phone installer is unavailable** - build the Android template, then publish with `-IncludeMobile`. Enable Phone
and configure pairing on the desktop; the source template itself is deliberately unconfigured.

**Something else broken?** That's expected at this stage - [open an issue](../../issues) and it gets looked at.

---

## License

[MIT](LICENSE) - do what you want with it, just keep the copyright notice.

VibeCode bundles no provider CLI. Claude Code, OpenAI Codex, Kimi Code, and Grok remain under their own licenses
and terms; you install and sign into them yourself. Third-party assets retain their accompanying notices.
