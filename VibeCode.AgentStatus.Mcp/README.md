# Agent status MCP server

A small stdio MCP host for reporting current activity to the desktop. It validates status messages and publishes versioned receipts. The desktop supplies runtime identity and access information separately.

The `report_status` tool accepts a stage label, a short activity description, a task title, an optional step, an optional current tool/action, and a state (`working`, `waiting`, `blocked`, or `completed`). Use `unknown` for work without an applicable MITRE tactic label. `list_stages` returns the supported vocabulary.

```json
{
  "stage": "unknown",
  "activity": "Reviewing the layout changes",
  "task_title": "Review dashboard layout",
  "step": "Check compact widths",
  "state": "working"
}
```

Reports are display metadata. They do not execute work or change permissions. The desktop binds receipts to the provider's runtime identity, and keeps root and child status separate. Avoid credentials, private paths, commands and raw tool output in reports.

## Desktop use

Enable **Agent activity monitor** in Settings to register reporting for Codex sessions. Open the monitor to view reports. Closing its window stops display refreshes while configured reporting continues. Disabling the setting removes the managed registration and stops logging.

## Standalone use

```powershell
dotnet build VibeCode.AgentStatus.Mcp/VibeCode.AgentStatus.Mcp.csproj
dotnet VibeCode.AgentStatus.Mcp/bin/Debug/net8.0/VibeCode.AgentStatus.Mcp.dll
```

The host reads newline-delimited MCP JSON-RPC from stdin and writes responses to stdout. Complete initialization before calling tools. The standalone sink retains a bounded in-memory history; the desktop adapter handles persistent logging when enabled.

The published desktop also hosts this protocol through `VibeCode.exe --agent-status-mcp`, without opening a desktop window. Each MCP client's stdio connection owns its helper process lifetime.
