# VibeCode

A Windows desktop app for Claude Code, OpenAI Codex, Gemini CLI, and GLM. Run coding chats and teams of agents, share MCP tools, switch accounts, and track provider-reported usage in one place.

This repository contains the public source. Personal accounts, credentials, chat history, downloaded tools, and compiled app payloads are not included.

## Getting started

Build the Windows app using the steps below, then open **VibeCode.exe**.

1. Open the account menu in the lower-left corner and choose a provider.
2. If its CLI is missing, click **Install Claude Code CLI**, **Install Codex CLI**, or **Install Gemini CLI**. Setup downloads the official CLI and required tools, verifies that the CLI starts, and continues to sign-in.
3. Complete the provider's sign-in flow, choose a project folder, and start a chat.

Setup supports cancellation and retry. It installs tools for the current Windows user under `%LOCALAPPDATA%\VibeCode\runtimes`; it does not require an administrator, a global npm installation, or manual copying into native-assets folders. Existing compatible CLIs and explicit path overrides remain supported. Installation needs an internet connection. GLM connects through an API account and does not need a CLI.

Provider accounts, subscriptions, and model access are managed by the provider. Installing a CLI does not include a subscription.

| Provider | Connection | Setup |
| --- | --- | --- |
| Claude Code | Official Claude Code CLI | Install in the account menu, then sign in with Claude |
| OpenAI Codex | Official Codex CLI | Install in the account menu, then sign in with ChatGPT |
| Gemini | Official Gemini CLI, tested with 0.63.0 | Install in the account menu, then sign in with Google; Google AI Pro and Ultra account access is supported |
| GLM | Z.ai Coding Plan, Z.ai API, or Baseten | Add an API account in the account menu |

Install sources: [Claude Code](https://code.claude.com/docs/en/setup), [Codex](https://learn.chatgpt.com/docs/codex/cli), and [Gemini CLI](https://geminicli.com/docs/get-started/installation/). The managed installer obtains Node.js from nodejs.org. Provider CLIs retain their own licenses.

## Coding and collaboration

- **Chats:** streamed answers, tool calls, permission prompts, file changes, attachments, reasoning controls, saved sessions, recovery, and queued follow-ups.
- **Bridges:** independent agents in one project, with shared activity, peer messages, task names, file-edit history, and per-agent accounts. Advanced bridges add orchestrators, workers, task dependencies, review settings, and recovery.
- **Provider subagents:** Claude and Codex can delegate when you request it. The app's current child-agent policy controls the worker limit and coordination.
- **MCP:** configure servers once in Settings. VibeCode maps the catalog to each provider's supported transport and exposes its bridge and status tools. Gemini supports the app's MCP integration through ACP, including stdio and remote server configurations.
- **Usage:** token totals, estimated cost, and provider quota information stay distinct. Streaming estimates reconcile with reported totals, including cached tokens, completed turns, partial turns, and restored sessions. Unavailable quota stays unavailable rather than appearing as zero usage.
- **Speed controls:** Codex fast and ultrafast controls sit beside reasoning effort. Availability follows model and account capability; ultrafast requires the qualifying account entitlement. Cost estimates and subscription usage use their respective multipliers.
- **Organization:** pin and lock chats, group them by directory, and use **Close All** on a directory to close its chats. Saved history remains available; locked-chat protection still applies.

Gemini's model choices follow the installed CLI's catalog and account access. The app does not invent a model entry that the CLI cannot launch. A newer Gemini model requires CLI support as well as account access.

## Desktop features

**Jarvis** provides a separate desktop-assistant conversation, configurable provider and voice, and supported Windows actions. Dictation uses the selected speech service; local speech models download when needed. Native speech work is serialized to avoid overlapping model lifetimes.

**Second Brain** is an optional extension with per-chat access controls. It requires a compatible memory service and must be enabled in Settings before use.

**Phone access** pairs the Android companion with the desktop app. Build the optional unpaired mobile template before publishing if you want APK generation included. Pairing state, signing keys, and preconfigured enrollment data must never be committed.

Closing the window can leave work running in the system tray when **Run in background** is enabled. Use **Quit** to exit, or disable background running in Settings.

## Build and test

Requirements: Windows 10/11 x64, the .NET 8 and .NET 10 SDKs (some regression projects target .NET 10), and Git. Node.js 24 is needed for the recorded Gemini protocol fixtures. Provider CLIs are not needed for the offline regression suite.

```powershell
git clone https://github.com/SeanDishman/vibecode.git
cd vibecode
./scripts/Test.ps1
./scripts/Publish.ps1
```

The publish script produces a self-contained Windows build in `artifacts/windows-x64/`. Its runtime dependencies are packaged by the build; the destination computer does not need the .NET SDK. Build outputs stay outside source control.

To run the development app:

```powershell
dotnet run --project VibeCode.Desktop
```

For an optional network integration check that downloads all three CLIs into an isolated temporary directory, without signing in or making model calls:

```powershell
dotnet run --project tests/VibeCode.CliInstallTests -- --install
```

The default test script covers provider catalogs, Gemini protocol/usage fixtures, CLI setup behavior, accounting, chat closing, bridges, orchestration, scrolling, and the unpaired mobile template. It makes no live model calls. Live account access and platform-specific audio hardware still need testing on the destination machine.

## Android template

Install JDK 17 and the Android SDK required by `mobile/VibeCodeMobile/app/build.gradle.kts`, then run:

```powershell
./scripts/Build-Mobile.ps1
./scripts/Publish.ps1 -IncludeMobile
```

The mobile build script downloads the Gradle distribution named in the wrapper properties and verifies its published SHA-256 checksum. `-Offline` uses an already cached distribution. No compiled Gradle wrapper or APK is committed. The desktop publish script validates that an included template is unpaired.

## Configuration

Settings and runtime data belong to the current Windows user, outside this checkout. Saved API keys use Windows user-bound encryption. Do not share your app-data or provider credential folders as part of a source update.

| Variable | Purpose |
| --- | --- |
| `VIBECODE_DATA_DIR` | Override the app's settings and data directory |
| `VIBECODE_RUNTIME_DIR` | Override the managed CLI tools directory |
| `VIBECODE_CLAUDE_PATH`, `VIBECODE_CODEX_PATH`, `VIBECODE_GEMINI_PATH` | Use a specific provider CLI |
| `CLAUDE_CONFIG_DIR`, `CODEX_HOME`, `GEMINI_CLI_HOME` | Provider configuration locations; managed accounts may select an account-specific home |

Do not put secrets in these paths inside the repository. The source policy check rejects credentials, compiled payloads, and private feature material before the normal test run. Review staged changes and run a secret scanner before publishing.

## Repository layout

```text
VibeCode.Desktop/        WPF app, provider protocols, UI, and services
VibeCode.AgentStatus.Mcp/ MCP bridge and agent-status contracts
mobile/VibeCodeMobile/  Android companion source and unpaired enrollment slot
tests/                 Isolated protocol, UI, accounting, and installation checks
scripts/               Source checks, build, test, and publish scripts
assets/                App branding
```

## Troubleshooting

- **CLI missing:** select the provider in the account menu and use its Install CLI button. A failed download can be retried from the same button.
- **Signed out or quota unavailable:** sign in through the provider's account flow. Model access and quota depend on that account; an unavailable quota response is not a zero balance.
- **Copied app fails to launch:** use the output of `scripts/Publish.ps1`, not a development build copied without its dependencies.
- **Bug or crash:** [open an issue](https://github.com/SeanDishman/vibecode/issues) with the app version, provider, and steps to reproduce. Remove account details, chat contents, and tokens from any logs you share.

VibeCode is under active development. See [LICENSE](LICENSE) for the source license.
