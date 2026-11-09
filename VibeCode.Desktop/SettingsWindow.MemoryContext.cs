using System.Windows;
using VibeCode.Services;

namespace VibeCode;

/// <summary>Memory and context settings beside the Second Brain card: the retention period, the checkpoint nudge and the
/// opt-in background memory review.</summary>
public partial class SettingsWindow
{
    /// <summary>Loads the saved values first, then listens, so opening Settings never rewrites them.</summary>
    private void InitializeMemoryContextSettings()
    {
        var s = AppSettings.Current;
        MemoryRetentionSlider.Value = Math.Clamp(s.MemoryRetentionDays, MemoryRetention.MinimumDays, MemoryRetention.MaximumDays);
        CheckpointNudgeSlider.Value = Math.Clamp(s.ContextCheckpointNudgePercent, 0, 95);
        BackgroundMemoryReviewToggle.IsChecked = s.BackgroundMemoryReview;
        MemoryRetentionSlider.ValueChanged += (_, e) => SaveMemorySetting(() => s.MemoryRetentionDays = (int)Math.Round(e.NewValue));
        CheckpointNudgeSlider.ValueChanged += (_, e) => SaveMemorySetting(() => s.ContextCheckpointNudgePercent = (int)Math.Round(e.NewValue));
        BackgroundMemoryReviewToggle.Checked += (_, _) => SaveMemorySetting(() => s.BackgroundMemoryReview = true);
        BackgroundMemoryReviewToggle.Unchecked += (_, _) => SaveMemorySetting(() => s.BackgroundMemoryReview = false);
    }

    // Each sweep and chat reads these when it runs, so nothing needs restarting.
    private void SaveMemorySetting(Action apply)
    {
        if (!_ready) return;
        apply();
        AppSettings.Current.Save();
    }
}
