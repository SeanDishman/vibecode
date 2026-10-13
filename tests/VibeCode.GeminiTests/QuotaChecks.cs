using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using VibeCode.Services;

internal static partial class Program
{
    private static async Task VerifyQuotaRefresh()
    {
        var credentials = Path.Combine(GeminiRuntime.ConfigDirectory, "oauth_creds.json");
        var accounts = Path.Combine(GeminiRuntime.ConfigDirectory, "google_accounts.json");
        var saved = File.ReadAllText(credentials);
        var encrypted = Environment.GetEnvironmentVariable("GEMINI_FORCE_ENCRYPTED_FILE_STORAGE");
        var project = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT");
        var projectId = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT_ID");
        try
        {
            Environment.SetEnvironmentVariable("GOOGLE_CLOUD_PROJECT", null);
            Environment.SetEnvironmentVariable("GOOGLE_CLOUD_PROJECT_ID", null);
            Environment.SetEnvironmentVariable("GEMINI_FORCE_ENCRYPTED_FILE_STORAGE", null);
            File.WriteAllText(credentials, new JsonObject { ["access_token"] = "quota-fixture", ["expiry_date"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds() }.ToJsonString());
            File.WriteAllText(accounts, "{\"active\":\"fixture@example.invalid\"}");
            using var handler = new QuotaHandler();
            using var http = new HttpClient(handler);
            var service = (GeminiUsageService)Activator.CreateInstance(typeof(GeminiUsageService), Flags, null, [http], null)!;
            async Task Refresh() => await (Task)typeof(GeminiUsageService).GetMethod("RefreshAsync", Flags)!.Invoke(service, [true])!;
            await Refresh();
            Check("quota authenticates read-only requests and resolves the Google project", handler.ValidRequests == 2 && handler.Calls == 2);
            Check("quota displays the returned plan and consumed fraction", service.Plan == "Google AI Pro" && service.Summary == "75% used");
            Check("shared model pools are never summed into an invented allowance", service.Limits.Count == 2 && service.Detail.Contains("375 requests remaining"));
            handler.Fail = true;
            await Refresh();
            Check("transient quota failure retains but clearly marks stale readings", service.HasData && service.Status.Contains("out of date"));
            File.WriteAllText(accounts, "{\"active\":\"different@example.invalid\"}");
            await Refresh();
            Check("switching Google accounts clears the previous account quota", !service.HasData && service.Summary == "Quota unavailable");
            var calls = handler.Calls;
            Environment.SetEnvironmentVariable("GEMINI_FORCE_ENCRYPTED_FILE_STORAGE", "true");
            Check("encrypted Google logins are recognized without decrypting credentials", GeminiRuntime.HasLogin && GeminiRuntime.AccountEmail == "different@example.invalid");
            await Refresh();
            Check("encrypted credentials cannot use a stale plaintext token", handler.Calls == calls && !service.HasData && service.Status.Contains("encrypted"));
        }
        finally
        {
            File.WriteAllText(credentials, saved);
            File.Delete(accounts);
            Environment.SetEnvironmentVariable("GEMINI_FORCE_ENCRYPTED_FILE_STORAGE", encrypted);
            Environment.SetEnvironmentVariable("GOOGLE_CLOUD_PROJECT", project);
            Environment.SetEnvironmentVariable("GOOGLE_CLOUD_PROJECT_ID", projectId);
        }
    }

    private sealed class QuotaHandler : HttpMessageHandler
    {
        public int Calls, ValidRequests;
        public bool Fail;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Fail) return new(HttpStatusCode.ServiceUnavailable);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var profile = request.RequestUri!.AbsolutePath.EndsWith(":loadCodeAssist");
            if (request.Headers.Authorization?.Parameter == "quota-fixture" && request.Method == HttpMethod.Post
                && request.RequestUri.Host == "cloudcode-pa.googleapis.com"
                && (profile ? body?["metadata"]?["pluginType"]?.ToString() == "GEMINI" : body?["project"]?.ToString() == "fixture-project")) ValidRequests++;
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(profile
                ? """{"cloudaicompanionProject":"fixture-project","paidTier":{"name":"Google AI Pro"}}"""
                : """{"buckets":[{"modelId":"gemini-3.1-pro-preview","remainingFraction":0.25,"remainingAmount":"375"},{"modelId":"gemini-3.8-flash","remainingFraction":0.25,"remainingAmount":"375"}]}""", Encoding.UTF8, "application/json")
            };
        }
    }
}
