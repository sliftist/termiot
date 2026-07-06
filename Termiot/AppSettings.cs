using System.IO;
using System.Text.Json;

namespace Termiot;

public sealed class AppSettings
{
    public bool ShowEscapeSequences { get; set; }
    public bool RawInput { get; set; }
    public bool AutoResumeShells { get; set; }
    public bool WriteLogImmediately { get; set; }
    public bool ReopenOnStartup { get; set; }

    public string OpenRouterConfigPath { get; set; } = "";
    public string LlmModel { get; set; } = "qwen/qwen3-coder-30b-a3b-instruct";
    public int LlmContextTokens { get; set; } = 10000;
    public bool LlmEnabled { get; set; }
    public bool LlmMultiComplete { get; set; }
    public bool LlmTriggerEnabled { get; set; }
    public string LlmTriggerPhrases { get; set; } = "Hey llm | llm please | hey ai | ai please";
    public int MultiCompleteCount { get; set; } = 3;
    public double LlmTotalCostUsd { get; set; }
    public long LlmInputTokens { get; set; }
    public long LlmOutputTokens { get; set; }
    public long LlmRequestCount { get; set; }
    public long LlmTotalLatencyMs { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new AppSettings();
            }
        }
        catch
        {
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }
}
