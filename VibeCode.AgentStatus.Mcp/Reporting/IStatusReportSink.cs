using VibeCode.AgentStatus.Mcp.Contracts;

namespace VibeCode.AgentStatus.Mcp.Reporting;

// Replace or compose this sink to add storage/subscribers without changing the MCP transport or UI.
// A standalone MCP connection has no authenticated per-agent identity. The desktop adapter supplies
// that from provider events; never use an AI-supplied thread id as an authority boundary.
public interface IStatusReportSink
{
    void Publish(AgentStatusReport report);
}

public sealed class InMemoryStatusReportSink : IStatusReportSink
{
    private readonly Queue<AgentStatusReport> _recent = new();
    public IReadOnlyList<AgentStatusReport> Recent => _recent.ToArray();
    public event Action<AgentStatusReport>? Reported;
    public void Publish(AgentStatusReport report)
    {
        _recent.Enqueue(report);
        while (_recent.Count > 64) _recent.Dequeue();
        Reported?.Invoke(report);
    }
}
