using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using VibeCode.Protocol;

namespace VibeCode.Services;

/// <summary>
/// Runs a short, isolated provider turn that drafts an MCP definition. The result is never persisted or executed here;
/// the settings dialog displays it and sends it through <see cref="McpCatalog.Validate"/> before the user may save it.
/// </summary>
public static class McpConfigAssistantService
{
    private const string SystemPrompt = """
        You draft MCP server configurations for VibeCode on Windows. Research current official documentation with web
        search when the requested server or package is ambiguous. Do not run shell commands, edit files, install
        packages, or ask questions. Choose the most likely official or well-established MCP server and explain any
        ambiguity in notes.

        Return exactly one JSON object and no markdown. Use this schema:
        {
          "name": "letters-numbers-hyphens-or-underscores",
          "transport": "stdio | http | sse",
          "command": "executable only, with no surrounding quotes",
          "arguments": ["one", "exact", "argument", "per", "item"],
          "environment": {"NAME": "value or ${NAME} placeholder"},
          "url": "https://... for remote transports, otherwise empty",
          "headers": {"Header-Name": "value or ${NAME} placeholder"},
          "bearerTokenEnvironmentVariable": "ENV_NAME or null",
          "startupTimeoutSeconds": 30,
          "toolTimeoutSeconds": 60,
          "useClaude": true,
          "useCodex": true,
          "useKimi": true,
          "useGrok": true,
          "notes": "Brief prerequisites and the official source URL used, if one was found."
        }

        Never output a real credential or invent one. Use environment-variable placeholders for secrets. Prefer a
        direct executable, npx with -y and a package name, uvx with a package name, or a documented remote URL. Do not
        place a whole command line in command; every argument must be a separate array item. Current Codex does not
        support legacy SSE, so set useCodex false for sse.
        """;

    public static async Task<McpConfigSuggestion> GenerateAsync(string provider, string? model, string request,
        string workingDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request))
            throw new ArgumentException("Describe the MCP server you want to add.", nameof(request));

        provider = string.IsNullOrWhiteSpace(provider) ? "claude" : provider.Trim().ToLowerInvariant();
        model = string.Equals(model, "default", StringComparison.OrdinalIgnoreCase) ? null : model;
        var cwd = Directory.Exists(workingDirectory) ? Path.GetFullPath(workingDirectory) : AppSettings.Dir;

        using var session = CreateSession(provider, model, cwd);
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<AssistantTurn>(TaskCreationOptions.RunContinuationsAsynchronously);
        var assistantMessages = new List<string>();
        var messageGate = new object();

        session.Initialized += () => initialized.TrySetResult();
        session.Exited += (code, stderr) =>
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? $"exit code {code}" : stderr.Trim();
            var error = new InvalidOperationException($"The {ProviderName(provider)} assistant exited before finishing: {detail}");
            initialized.TrySetException(error);
            completed.TrySetException(error);
        };
        session.PermissionRequested += requestInfo => session.RespondPermission(requestInfo.RequestId, new JsonObject
        {
            ["behavior"] = "deny",
            ["message"] = "MCP configuration assistance may research, but it may not execute local tools or modify files.",
        }, requestInfo.ToolUseId);
        session.MessageReceived += node =>
        {
            if (node["type"]?.GetValue<string>() == "assistant"
                && node["message"]?["content"] is JsonArray blocks)
            {
                foreach (var block in blocks.OfType<JsonObject>())
                {
                    if (block["type"]?.GetValue<string>() != "text") continue;
                    var text = NodeText(block["text"]);
                    if (text.Length == 0) continue;
                    lock (messageGate) assistantMessages.Add(text);
                }
            }

            if (node["type"]?.GetValue<string>() != "result") return;
            var resultText = NodeText(node["result"]);
            var subtype = NodeText(node["subtype"]);
            var isError = ReadBool(node["is_error"])
                          || subtype.Contains("error", StringComparison.OrdinalIgnoreCase)
                          || subtype.Contains("fail", StringComparison.OrdinalIgnoreCase);
            completed.TrySetResult(new AssistantTurn(resultText, isError, subtype));
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            session.Start();
            await initialized.Task.WaitAsync(timeout.Token);
            session.SendUser(JsonValue.Create("Draft an MCP configuration for this request: " + request.Trim()));
            var turn = await completed.Task.WaitAsync(timeout.Token);

            List<string> candidates;
            lock (messageGate) candidates = assistantMessages.AsEnumerable().Reverse().ToList();
            if (!string.IsNullOrWhiteSpace(turn.ResultText)) candidates.Insert(0, turn.ResultText);
            if (turn.IsError)
                throw new InvalidOperationException(candidates.FirstOrDefault()
                    ?? $"The {ProviderName(provider)} assistant could not generate a configuration ({turn.Subtype}).");

            foreach (var candidate in candidates)
                if (McpConfigSuggestionParser.TryParse(candidate, out var suggestion)) return suggestion;

            throw new InvalidOperationException(
                $"{ProviderName(provider)} replied, but its configuration was not valid JSON. Try rewording the request or selecting another model.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{ProviderName(provider)} did not finish the MCP configuration within three minutes.");
        }
    }

    private static ICodingSession CreateSession(string provider, string? model, string cwd) => provider switch
    {
        "codex" => new CodexSession(new CodexSessionOptions
        {
            Cwd = cwd,
            HomeDirectory = CodexAccountService.Instance.HomeFor(CodexAccountService.Instance.ActiveId),
            Model = model,
            PermissionMode = "default",
            AppendSystemPrompt = SystemPrompt,
            McpServers = Array.Empty<McpServerDefinition>(),
        }),
        "kimi" => new KimiSession(new KimiSessionOptions
        {
            Cwd = cwd,
            Model = model,
            PermissionMode = "default",
            AppendSystemPrompt = SystemPrompt,
            McpServers = Array.Empty<McpServerDefinition>(),
        }),
        "grok" => new GrokSession(new GrokSessionOptions
        {
            Cwd = cwd,
            Model = model,
            PermissionMode = "default",
            AppendSystemPrompt = SystemPrompt,
            McpServers = Array.Empty<McpServerDefinition>(),
        }),
        _ => new ClaudeSession(new ClaudeSessionOptions
        {
            Cwd = cwd,
            Model = model,
            PermissionMode = "default",
            AppendSystemPrompt = SystemPrompt,
            ConfigDirectory = AccountService.Instance.ConfigDirectory(AppSettings.Current.ActiveAccountId),
            McpServers = Array.Empty<McpServerDefinition>(),
        }),
    };

    private static bool ReadBool(JsonNode? node, bool fallback = false)
    {
        if (node is null) return fallback;
        try { return node.GetValue<bool>(); }
        catch { return bool.TryParse(NodeText(node), out var value) ? value : fallback; }
    }

    private static string NodeText(JsonNode? node)
    {
        if (node is null) return "";
        try { return node.GetValue<string>(); }
        catch { return node.ToString(); }
    }

    private static string ProviderName(string provider) => provider switch
    {
        "codex" => "OpenAI Codex",
        "kimi" => "Kimi Code",
        "grok" => "Grok",
        _ => "Claude Code",
    };

    private sealed record AssistantTurn(string ResultText, bool IsError, string Subtype);
}
