# Workflow Telemetry Wall

Open **Settings > Usage > Live telemetry > Workflow Telemetry Wall**. This is a separate window; the existing usage HUD and telemetry wall are unchanged.

Projects are hubs connected to agents working now or active in the last 60 minutes. Idle agents disappear automatically after an hour, and directories with no recent agents disappear with them. Merely opening an old chat or starting its CLI does not count as agent work. Genuine assistant activity can supply the timestamp for older sessions. Background and second-monitor Bridges remain eligible. Directories use their full normalized Windows path, so matching folder names on different drives stay separate. Scratch conversations are grouped under No directory.

- Filter by project, search by project/agent/model/task, or show working agents only.
- Click a project hub to focus its directory; Clear restores the overview.
- Click an agent to open its existing chat or Bridge pane, including minimized or parked panes.
- Working connections move; waiting, stopped, idle and error states do not. Motion is an activity indicator, not measured message traffic.
- Active provider-native subagents are counted on their parent and in the summary. The graph's individual nodes represent open chats and Bridge panes.
- F11 toggles full screen; Escape closes the wall. Closing it never stops chats.

The wall inherits the current app theme, companion-display setting and telemetry animation preference, and respects Windows reduced-motion settings. Narrow windows use a single directory tree; wide displays arrange independent project clusters side by side. Larger rosters scroll without discarding agents.

Directory hubs have folder icons and accent outlines, while agent nodes emphasize their task and show a labeled status dot. The graph starts at the top of its canvas. In narrow windows each directory stays beside its first agent. Long labels truncate inside the node and remain available in full through its tooltip; keyboard order follows the visible project/agent order after filtering.

Below the map, the usage section shows total, input, output and cache-read tokens for the exact rolling hour. It includes a five-minute timeline and an input/output comparison by model. These charts are explicitly **all projects**, including recently completed or closed chats; map search and project filters do not change their scope. Input already includes cache-read and cache-write tokens, so the cache figure is not an additional charge to the total. History is grouped by turn timestamp, with provisional live turns replaced by committed usage through the existing accounting path.

The window reads live view-model state once per second while visible. Unchanged snapshots retain their controls; animation repaints only cached connection geometry at 20 FPS while agents are working. Usage reads the local ledger and existing live-turn telemetry, without making provider or quota requests. No transcript or stored-memory access is performed by the wall. Narrow windows stack the two charts below the map; the full wall can scroll when needed.

Verification: `dotnet run --project tests/VibeCode.TelemetryTests/VibeCode.TelemetryTests.csproj -c Release`. Tests use local fixtures, exercise grouping and lifecycle/navigation, and render compact, desktop, wall, empty and CLI-theme captures. Generated binaries follow the repository BuildOutput rules.

Visual interaction checks additionally cover long names, 150% raster scaling, keyboard focus and scroll-into-view, node activation, timed state updates, stationary versus animated connection frames, the exact activity cutoff, cache accounting, bucket/total agreement, and provisional-to-committed usage. Captures show the production WPF controls with isolated fixture chats and token data, not a live account session.
