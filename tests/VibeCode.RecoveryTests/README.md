# Compact, recovery, and bridge feature checks

Run on Windows with the .NET 8 SDK from the workspace root:

```powershell
dotnet run --project SRC/tests/VibeCode.RecoveryTests/VibeCode.RecoveryTests.csproj
dotnet run --project SRC/tests/VibeCode.GoalTests/VibeCode.GoalTests.csproj
dotnet run --project SRC/tests/VibeCode.OrchestratorGroupTests/VibeCode.OrchestratorGroupTests.csproj -- --groups
dotnet run --project SRC/tests/VibeCode.OrchestratorGroupTests/VibeCode.OrchestratorGroupTests.csproj -- --planning-recovery
```

Build these projects sequentially: they reference the same WPF desktop project. Each harness uses a separate settings directory and synthetic sessions or local provider fixtures. A failure must return a nonzero exit code. Screenshots and provider traces are written below `artifacts`.

`RecoveryTests` covers native Claude command serialization and Codex compaction RPC/completion, command guards and errors, recovery state transitions, the actual dispatcher timer, bounded repeat delays, preserved queued work, and cancellation races. Provider allowance checks exercise fresh Codex RPC reads, Kimi HTTP/token handling through a loopback server, GLM account rotation and stale refreshes through an HTTP fixture, and Claude model-specific report parsing. The provider/role matrix uses synthetic sessions: it checks the shared chat state machine, not five live subscription resets.

The same harness checks settings on disk and after reload, parent and multiple-subagent activity, shared-terminal property notifications and rendered orb bindings, sidebar activity for visible and background bridges, explicit independent role models/efforts, cross-provider planner adapters, and a Claude manager with GLM workers. The recovery matrix includes normal chats, regular bridges, Advanced Bridge workers, and Advanced Bridge orchestrators. `GoalTests` checks that `/goal` remains functional and `/compact` appears and preserves composer bindings in normal, split-bridge, shared-terminal, and CLI-theme views.

`--groups` checks model/effort propagation through local Claude and Codex CLI processes, central assignments, worker ownership, persistence, and responsive setup layouts. `--planning-recovery` requires one provider process and thread, a fresh allowance read between prompts, exactly one continuation, and disposal after completion or cancellation. Successful output or a waiting callback alone is insufficient to pass these checks.

For an additional check of test sensitivity:

```powershell
& SRC/tests/VibeCode.RecoveryTests/Verify-RegressionSensitivity.ps1
```

The script first requires a healthy build and passing tests. It then builds two isolated source copies: one sends compaction to the wrong Codex RPC, and one omits the shared orb activity notification. Each copy must compile and fail its intended behavioral assertion with exit code 1. A compilation failure, unrelated failure, or passing mutant fails the script. It leaves workspace source unchanged and saves build/test logs under `artifacts/test-sensitivity`. `-SkipHealthyRun` is available only when the healthy suite was already run against the current code.

These checks do not contact live subscription endpoints or spend provider quota. Live account limits and provider schema changes still require an authenticated integration check; local fixtures establish the app's behavior for the documented inputs.
