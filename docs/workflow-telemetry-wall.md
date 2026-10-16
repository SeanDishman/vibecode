# Workflow Telemetry Wall

Open **Settings > Usage > Live telemetry > Workflow Telemetry Wall**. This is a separate window; the existing usage HUD and telemetry wall are unchanged.

Projects are hubs connected to their open chats and Bridge agents. Background and second-monitor Bridges remain represented. Directories use their full normalized Windows path, so matching folder names on different drives stay separate. Scratch conversations are grouped under No directory.

- Filter by project, search by project/agent/model/task, or show working agents only.
- Click a project hub to focus its directory; Clear restores the overview.
- Click an agent to open its existing chat or Bridge pane, including minimized or parked panes.
- Working connections move; waiting, stopped, idle and error states do not. Motion is an activity indicator, not measured message traffic.
- Active provider-native subagents are counted on their parent and in the summary. The graph's individual nodes represent open chats and Bridge panes.
- F11 toggles full screen; Escape closes the wall. Closing it never stops chats.

The wall inherits the current app theme, companion-display setting and telemetry animation preference, and respects Windows reduced-motion settings. Narrow windows use a single directory tree; wide displays arrange independent project clusters side by side. Larger rosters scroll without discarding agents.

The window reads live view-model state once per second while visible. Unchanged snapshots retain their controls; animation repaints only cached connection geometry at 20 FPS while agents are working. No provider calls, usage-service requests, chat-history reads or stored-memory access are performed by the wall.

Verification: `dotnet run --project tests/VibeCode.TelemetryTests/VibeCode.TelemetryTests.csproj -c Release`. Tests use local fixtures, exercise grouping and lifecycle/navigation, and render compact, desktop, wall, empty and CLI-theme captures. Generated binaries follow the repository BuildOutput rules.
