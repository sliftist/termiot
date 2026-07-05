using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Termiot;

public sealed record LlmMessage(string Role, string Content);

public sealed class LlmModelInfo
{
    public string Id = "";
    // USD per token, from OpenRouter's pricing strings.
    public double PromptPrice;
    public double CompletionPrice;
}

public sealed class LlmCompletion
{
    public List<string> Lines = new();
    public int PromptTokens;
    public int CompletionTokens;
    public double CostUsd;
    public long LatencyMs;
}

public static class OpenRouter
{
    private const string BaseUrl = "https://openrouter.ai/api/v1";
    private const int RequestTimeoutSeconds = 30;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds) };

    // The config file is either the raw api key, or JSON with a "key" field.
    public static string? LoadKey(string configPath)
    {
        try
        {
            if (configPath.Length == 0 || !File.Exists(configPath))
            {
                return null;
            }
            var content = File.ReadAllText(configPath).Trim();
            if (content.StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(content);
                return doc.RootElement.TryGetProperty("key", out var key) ? key.GetString() : null;
            }
            return content.Length > 0 ? content : null;
        }
        catch (Exception ex)
        {
            AppLog.Write("openrouter", "key load failed: " + ex.Message);
            return null;
        }
    }

    public static async Task<List<LlmModelInfo>> GetModels(string apiKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var models = new List<LlmModelInfo>();
        foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            var info = new LlmModelInfo { Id = item.GetProperty("id").GetString() ?? "" };
            if (item.TryGetProperty("pricing", out var pricing))
            {
                info.PromptPrice = ParsePrice(pricing, "prompt");
                info.CompletionPrice = ParsePrice(pricing, "completion");
            }
            if (info.Id.Length > 0)
            {
                models.Add(info);
            }
        }
        return models;
    }

    private static double ParsePrice(JsonElement pricing, string field)
    {
        if (pricing.TryGetProperty(field, out var value) && double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var price))
        {
            return price;
        }
        return 0;
    }

    public static async Task<LlmCompletion> Complete(string apiKey, string model, List<LlmMessage> messages, int maxTokens, LlmModelInfo? pricing)
    {
        var payload = new
        {
            model,
            max_tokens = maxTokens,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var watch = Stopwatch.StartNew();
        using var response = await Http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        watch.Stop();
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"OpenRouter {(int)response.StatusCode}: {body}");
        }
        using var doc = JsonDocument.Parse(body);
        var result = new LlmCompletion { LatencyMs = watch.ElapsedMilliseconds };
        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim().TrimStart('$', '>').Trim().Trim('`').Trim();
            if (line.Length > 0)
            {
                result.Lines.Add(line);
            }
        }
        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            result.PromptTokens = usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
            result.CompletionTokens = usage.TryGetProperty("completion_tokens", out var ct) ? ct.GetInt32() : 0;
        }
        if (pricing != null)
        {
            result.CostUsd = result.PromptTokens * pricing.PromptPrice + result.CompletionTokens * pricing.CompletionPrice;
        }
        return result;
    }
}
