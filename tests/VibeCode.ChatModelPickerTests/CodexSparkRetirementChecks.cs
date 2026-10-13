using System.Reflection;
using System.Text.Json.Nodes;
using VibeCode.Protocol;
using VibeCode.Services;
using VibeCode.UI;

internal static partial class Program
{
    private static void VerifySparkRetirement()
    {
        const string spark = "gpt-5.3-codex-spark";
        const string luna = "gpt-6-luna";
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var chat = Chat("codex");
        Check("offline GPT picker excludes retired Spark", chat.PickerModels.All(row => row.Value != spark));
        Check("offline GPT picker retains supported Luna", chat.PickerModels.Any(row => row.Value == luna));

        chat.Models.Add(new ModelChoice { Provider = "codex", Value = spark, Display = "GPT-5.3 Codex Spark" });
        chat.Models.Add(new ModelChoice { Provider = "codex", Value = luna, Display = "GPT-6 Luna" });
        chat.RefreshModelPicker();
        Check("a stale live catalog cannot restore Spark to the picker",
            chat.PickerModels.Count == 1 && chat.PickerModels[0].Value == luna);
        chat.SetModel(luna);
        chat.SetPickerModel(new ModelPickerChoice
        {
            Model = new ModelChoice { Provider = "codex", Value = spark, Display = "GPT-5.3 Codex Spark" },
            CanApply = true,
        });
        Check("a stale Spark selection leaves the valid chat model intact", chat.Model == luna);

        var savedDefault = AppSettings.Current.DefaultCodexModel;
        try
        {
            AppSettings.Current.DefaultCodexModel = spark;
            var restored = Chat("codex", resume: "retired-spark-chat");
            Check("a saved Spark default does not select the retired model", restored.Model is null);
            Check("a restored Spark chat offers supported models", restored.PickerModels.Any(row => row.Value == luna)
                && restored.PickerModels.All(row => row.Value != spark));
        }
        finally { AppSettings.Current.DefaultCodexModel = savedDefault; }

        using var session = new CodexSession(new CodexSessionOptions { Cwd = Root, Model = spark });
        var selection = typeof(CodexSession).GetField("_model", flags)!;
        var buildModels = typeof(CodexSession).GetMethod("BuildModels", flags)!;
        Check("saved Spark is cleared before the provider launches", selection.GetValue(session) is null);
        buildModels.Invoke(session, [null]);
        Check("the protocol's offline model catalog does not invent Spark",
            session.Models.OfType<JsonObject>().All(row => row["value"]?.GetValue<string>() != spark));
        buildModels.Invoke(session, [new JsonArray
        {
            new JsonObject { ["id"] = spark, ["displayName"] = "GPT-5.3 Codex Spark", ["isDefault"] = true },
            new JsonObject { ["id"] = luna, ["displayName"] = "GPT-6 Luna", ["isDefault"] = true },
        }]);
        Check("the protocol excludes Spark even when a stale provider catalog returns it",
            session.Models.OfType<JsonObject>().All(row => row["value"]?.GetValue<string>() != spark));
        session.SetModelAsync(spark, "medium").GetAwaiter().GetResult();
        Check("selecting retired Spark falls back to the live recommended model", (string?)selection.GetValue(session) == luna);
        Check("Spark is retired regardless of ID casing", CodexModelCatalog.IsRetiredModel("GPT-5.3-CODEX-SPARK"));
    }
}
