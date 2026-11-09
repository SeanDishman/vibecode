using System.Collections;
using System.Reflection;
using System.Text.Json.Nodes;
using VibeCode.Protocol;
using VibeCode.Services;

internal static class ProtocolBoundaryTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run()
    {
        McpChoicesStayIsolated();
        ApprovalResolutionKeepsWorking();
        foreach (var reason in new[] { "Content policy", "Safety policy", "Safety refusal", "Request refused", "Model refusal" })
            Require(!BridgeErrorRecovery.CanRetry(new JsonObject { ["is_error"] = true, ["result"] = reason }),
                "Policy stops must never schedule an automatic continuation");
        Require(BridgeErrorRecovery.CanRetry(new JsonObject { ["is_error"] = true, ["result"] = "HTTP 503 service unavailable" }),
            "Ordinary provider outages retain recovery");
        Console.WriteLine("PASS: configured MCP servers, per-chat isolation, and approval resolution");
    }

    private static void McpChoicesStayIsolated()
    {
        var configured = new List<McpServerDefinition>
        {
            new() { Id = "example-tools",Name = "example-tools",Command = "dotnet",Arguments = ["--info"] }        };
        var firstChat = McpCatalog.Snapshot(configured);
        firstChat[0].Arguments.Add("chat-specific-argument");
        firstChat.Clear();
        Require(configured.Count == 1 && configured[0].Arguments.SequenceEqual(new[] { "--info" }),
            "A chat's MCP selection must not mutate saved settings or another chat");

        var secondChat = McpCatalog.Snapshot(configured);
        var projection = McpCatalog.BuildCodexProjection(secondChat);
        Require(projection.ConfigOverrides.Any(value => value.Contains("dotnet", StringComparison.Ordinal)),
            "Configured tools must reach the provider's launch configuration");
        var empty = McpCatalog.BuildCodexProjection(Array.Empty<McpServerDefinition>());
        Require(empty.ConfigOverrides.Count == 0 && empty.Environment.Count == 0,
            "An empty tool catalog must produce an empty MCP projection");
    }

    private static void ApprovalResolutionKeepsWorking()
    {
        using var session = new CodexSession(new CodexSessionOptions { Cwd = Environment.CurrentDirectory });
        PermissionRequest? permission = null;
        string? cancelled = null;
        session.PermissionRequested += request => permission = request;
        session.PermissionCancelled += requestId => cancelled = requestId;
        typeof(CodexSession).GetMethod("PrimeNotificationFixture", Hidden)!.Invoke(session, ["test-root"]);
        var request = new JsonObject
        {
            ["threadId"] = "test-root",
            ["turnId"] = "test-turn",
            ["itemId"] = "test-command",
            ["command"] = "dotnet --version",
            ["cwd"] = Environment.CurrentDirectory
        };
        typeof(CodexSession).GetMethod("HandleServerMessage", Hidden)!.Invoke(session,
            ["item/commandExecution/requestApproval", request, JsonValue.Create(17)!]);
        Require(permission is not null && permission.Input?["command"]?.GetValue<string>() == "dotnet --version",
            "A provider approval request must still reach the app's approval UI");

        // Some provider versions resolve approvals without including a thread ID.
        typeof(CodexSession).GetMethod("HandleNotificationFixture", Hidden)!.Invoke(session,
            ["serverRequest/resolved", new JsonObject { ["requestId"] = 17 }]);
        Require(cancelled == permission!.RequestId, "Resolving an approval must dismiss the matching prompt");
        foreach (var field in new[] { "_approvalRpcIds", "_approvalMethods", "_approvalParams" })
            Require(((IDictionary)typeof(CodexSession).GetField(field, Hidden)!.GetValue(session)!).Count == 0,
                "Resolved approvals must not leave stale request state");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
