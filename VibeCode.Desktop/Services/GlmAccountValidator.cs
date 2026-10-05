using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using VibeCode.Protocol;

namespace VibeCode.Services;

/// <summary>Validate official accounts on the exact service the user selected, without assuming /models exists.</summary>
public static class GlmAccountValidator
{
    public static async Task<ApiKeyValidation> ValidateAsync(HttpClient http, string backend, string key,
        CancellationToken ct = default)
    {
        if (!GlmPreset.IsZai(backend)) throw new ArgumentException("Select an official Z.ai service.", nameof(backend));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{GlmPreset.BaseUrlFor(backend)}/chat/completions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            // A single output token proves authentication and plan access. Thinking is mandatory on GLM 5.3.
            request.Content = new StringContent(new JsonObject
            {
                ["model"] = GlmPreset.ZaiDefaultModelId,
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Hi" }),
                ["stream"] = false,
                ["max_tokens"] = 1,
                ["thinking"] = new JsonObject { ["type"] = "enabled" },
                ["reasoning_effort"] = "low",
            }.ToJsonString(), Encoding.UTF8, "application/json");

            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            var error = GlmApiError.Parse(body);
            if (response.IsSuccessStatusCode && error.Code is null && error.Message is null)
            {
                // Reject a proxy's HTML login/maintenance page even if it reports HTTP 200.
                try
                {
                    if ((JsonNode.Parse(body) as JsonObject)?["choices"] is JsonArray { Count: > 0 })
                        return ApiKeyValidation.Good($"{GlmPreset.BackendName(backend)} connected.");
                }
                catch (System.Text.Json.JsonException) { }
                return ApiKeyValidation.Bad("Z.ai returned an unexpected response. Try again in a moment.");
            }
            // Preserve real quota/expiry/product errors. HTTP 429 alone does not prove that a key works.
            if (error.Code is not null || error.Message is not null)
                return ApiKeyValidation.Bad(error.Describe(backend));
            return ApiKeyValidation.Bad((int)response.StatusCode is 401 or 403
                ? "Z.ai rejected that key. Copy the key from the selected account's API Keys page."
                : $"Couldn't check the Z.ai account (HTTP {(int)response.StatusCode}). Try again in a moment.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TaskCanceledException) { return ApiKeyValidation.Bad("Timed out reaching Z.ai. Try again in a moment."); }
        catch (HttpRequestException) { return ApiKeyValidation.Bad("Couldn't reach Z.ai. Check your connection and try again."); }
        catch (FormatException) { return ApiKeyValidation.Bad("Paste only the API key, without a header or line breaks."); }
    }
}
