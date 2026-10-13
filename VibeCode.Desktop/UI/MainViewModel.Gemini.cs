using VibeCode.Services;

namespace VibeCode.UI;

public sealed class GeminiAccountEntry
{
    public string Label { get; init; } = "";
    public string ProviderLine { get; init; } = "";
    public string UsageDisplay { get; init; } = "";
    public string Initial => "G";
    public bool IsVibeCodeSelected { get; init; }
    public bool Usable { get; init; }
}

public sealed partial class MainViewModel
{
    private void OnGeminiUsageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GeminiUsageService.Summary)) return;
        Raise(nameof(GeminiAccountLabel)); Raise(nameof(GeminiAccountSub));
        Raise(nameof(AccountLabel)); Raise(nameof(AccountSub));
        RebuildAllAccounts();
    }

    private bool GeminiProviderSelected => AppSettings.Current.DefaultProvider == "gemini";
    public bool IsGeminiSignedIn => GeminiRuntime.IsInstalled && GeminiRuntime.HasLogin;
    public bool IsGeminiSelected => GeminiProviderSelected && IsGeminiSignedIn;
    public string GeminiAccountLabel => !GeminiRuntime.IsInstalled ? "Install Gemini CLI"
        : IsGeminiSignedIn ? GeminiRuntime.AccountEmail ?? "Google account" : "Sign in with Google";
    public string GeminiAccountSub => IsGeminiSignedIn ? "Gemini · " + GeminiUsageService.Instance.Plan
        : "Gemini · Google AI Pro, Ultra, or free";
    public bool BridgeUsesGemini => BridgePanes.Any(p => p.IsGemini);
    public bool SecondaryBridgeUsesGemini => SecondaryBridgePanes.Any(p => p.IsGemini);
    public void RefreshGeminiAccount()
    {
        Raise(nameof(IsGeminiSignedIn)); Raise(nameof(IsGeminiSelected));
        Raise(nameof(GeminiAccountLabel)); Raise(nameof(GeminiAccountSub));
        RefreshProviderPresentation();
    }
}
