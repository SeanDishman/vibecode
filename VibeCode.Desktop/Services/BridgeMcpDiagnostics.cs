using System.IO;
using System.Text.Json.Nodes;
using VibeCode.AgentStatus.Mcp.Bridge;

namespace VibeCode.Services;

internal static class BridgeMcpDiagnostics
{
    internal static BridgeMcpException Failure(Exception exception, string? tool, string phase)
    {
        var storage = exception is IOException or UnauthorizedAccessException;
        var roster = exception is BridgeMailboxRosterException;
        var id = Guid.NewGuid().ToString("N");
        var reason = exception.Message.Replace('\r', ' ').Replace('\n', ' ');
        if (reason.Length > 1000) reason = reason[..1000] + "...";
        var error = new BridgeMcpException(exception is UnauthorizedAccessException ? "storage_access_denied"
                : storage ? "storage_unavailable" : roster ? "mailbox_roster_conflict" : "internal_error",
            (storage ? "VibeCode could not access Bridge mailbox storage. " : roster ? "VibeCode could not reconcile the Bridge mailbox roster. " : "VibeCode failed inside the Bridge handler. ") +
                exception.GetType().Name + ": " + reason,
            (storage ? "Restore mailbox storage access. " : "Report this diagnostic_id with the VibeCode log if the failure persists. ") +
                BridgeMcpException.CheckReceipt, "unknown", new JsonObject
                {
                    ["diagnostic_id"] = id, ["exception_type"] = exception.GetType().Name,
                }).WithContext(tool, phase);
        // Do not log the request, arguments or message body. The ID joins the tool receipt to this local stack.
        CrashLog.Note("bridge-mcp", new JsonObject
        {
            ["diagnostic_id"] = id, ["tool"] = tool, ["phase"] = phase,
            ["exception_type"] = exception.GetType().Name, ["stack_trace"] = exception.StackTrace,
        }.ToJsonString());
        return error;
    }
}
