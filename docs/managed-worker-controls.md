# Advanced Bridge worker controls

An orchestrator can stop and restart only the workers whose `coordinator_id` matches its own `agent_id`. Regular peers, unassigned agents, other orchestrators and their workers are rejected by the app on every call.

- `bridge_stop_worker(recipient, reason)` requests an interrupt, pauses queued work and cancels automatic provider retries. The chat, transcript, drafts, existing file changes and task IDs remain intact. `stop_pending: true` means the provider or prompt preparation has not finished stopping. The orchestrator receives a notification when the worker settles.
- `bridge_retry_task(task_id, message?)` restarts a failed, blocked or interrupted assignment after the worker settles. The optional message replaces the work order for the next attempt; omitting it preserves the previous instructions. The task ID, dependencies and attempt history remain. Inspect existing changes before retrying because a stopped turn may have already performed actions.
- `bridge_resume_worker(recipient)` releases held queued work, reconnecting the same conversation if needed. Interrupted assignments require `bridge_retry_task` instead. With no pending work, the worker stays idle; use `bridge_dispatch_task` for a new assignment.

Stopping is allowed before scope agreement. Restarting still requires the team's existing coordination rules, ownership checks and sign-in. The stop hold persists across saved Bridge recovery. A journal failure is reported without preventing a stop; restart requires a successful journal write.

Stop uses the provider's interrupt mechanism. It does not close a pane, delete history, undo file changes, or forcibly kill external processes. A provider that has not acknowledged the interrupt remains busy and cannot race a replacement turn.

Regression checks: run `VibeCode.BridgeTerminalTests --worker-controls-only` after rebuilding its project. This covers ownership isolation, durable state, queued input, preparation races, storage failures and an offline CLI process using the production Codex adapter. No live model requests are needed.
