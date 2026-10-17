# Workflow Telemetry Wall

Open **Settings > Usage > Live telemetry > Workflow Telemetry Wall**. This is a separate window; the existing usage HUD and telemetry wall are unchanged.

Projects are hubs connected to agents working now or active within the last 10 minutes. Idle agents disappear at the ten-minute cutoff, and directories with no recent agents disappear with them. Merely opening an old chat or starting its CLI does not count as agent work. Genuine assistant activity can supply the timestamp for older sessions. Background and second-monitor Bridges remain eligible. Directories use their full normalized Windows path, so matching folder names on different drives stay separate. Scratch conversations are grouped under No directory.

Advanced Bridges include the temporary central planning session, permanent orchestrators, and their assigned workers. Their graph follows project → central planner → orchestrator → worker. A planner is observed through the existing planning session, without starting a second provider or adding a chat. Clicking it opens its Bridge. Completed planners expire ten minutes after finishing; older saved plans without a timestamp are not presented as recent. Inactive orchestrators also expire while their working children remain visible.

- Filter by project, search by project/agent/model/task, or show working agents only.
- Click a project hub to focus its directory; Clear restores the overview.
- Click an agent to open its existing chat or Bridge pane, including minimized or parked panes.
- Working connections move; waiting, stopped, idle and error states do not. Motion is an activity indicator, not measured message traffic.
- Active provider-native subagents are counted on their parent and in the summary. The graph's individual nodes represent open chats and Bridge panes.
- F11 toggles full screen; Escape closes the wall. Closing it never stops chats.

The wall inherits the current app theme, companion-display setting and telemetry animation preference, and respects Windows reduced-motion settings. Narrow windows use a single directory tree; wide displays arrange independent project clusters side by side. Larger rosters scroll without discarding agents.

Directory hubs have folder icons and accent outlines, while agent nodes emphasize their task and show a labeled status dot. The graph starts at the top of its canvas. In narrow windows each directory stays beside its first agent. Long labels truncate inside the node and remain available in full through its tooltip; keyboard order follows the visible project/agent order after filtering.

The bottom-right analytics panel occupies about one fifth of the workspace on desktop and wall displays. It shows total, input, output, cache-read and cache-write tokens, cache-hit percentage, turns, model count, tokens per turn, average tokens per minute over the hour, and average tokens per minute over the latest five minutes. It retains a five-minute timeline and an input/output comparison by model. These charts cover the exact rolling hour across **all projects**, including recently completed or closed chats; the ten-minute agent cutoff and map filters do not change their scope. Input already includes cache tokens. Rates use recorded turn timestamps rather than claiming instantaneous provider generation speed; live turns are provisional and replaced by committed usage through the existing accounting path.

The window reads live view-model state once per second while visible. Unchanged snapshots retain their controls; animation repaints only cached connection geometry at 20 FPS while agents are working. Usage reads the local ledger and existing live-turn telemetry, without making provider or quota requests. No transcript or stored-memory access is performed by the wall. Narrow windows move the compact analytics panel beneath the map, aligned to the right; both regions can scroll without shrinking their text.

Verification: `dotnet run --project tests/VibeCode.TelemetryTests/VibeCode.TelemetryTests.csproj -c Release`. Tests use local fixtures, exercise grouping and lifecycle/navigation, and render compact, desktop, wall, empty and CLI-theme captures. Generated binaries follow the repository BuildOutput rules.

Visual interaction checks additionally cover long names, 150% raster scaling, keyboard focus and scroll-into-view, node activation, timed state updates, stationary versus animated connection frames, the exact activity cutoff, cache accounting, bucket/total agreement, and provisional-to-committed usage. Captures show the production WPF controls with isolated fixture chats and token data, not a live account session.
