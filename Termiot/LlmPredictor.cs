namespace Termiot;

// Command prediction with exactly one outstanding OpenRouter request: new snapshots arriving while a request is in flight replace the pending one, and the worker keeps going until it has answered the latest snapshot. Cost/token/latency stats accumulate into settings.
public sealed class LlmPredictor
{
    private const int BaseMaxTokens = 60;
    private const int PerSuggestionMaxTokens = 80;
    private const int MaxTokensCap = 2000;

    private readonly AppSettings _settings;
    private readonly object _lock = new();
    private (List<LlmMessage> Messages, string ContextDisplay, int Count)? _pending;
    private bool _busy;
    private List<LlmModelInfo>? _models;

    public IReadOnlyList<string> Suggestions { get; private set; } = Array.Empty<string>();
    public string LastContext { get; private set; } = "";
    public string LastError { get; private set; } = "";
    public event Action? Updated;

    public LlmPredictor(AppSettings settings)
    {
        _settings = settings;
    }

    public bool IsConfigured => OpenRouter.LoadKey(_settings.OpenRouterConfigPath) != null && _settings.LlmModel.Length > 0;

    public async Task<List<LlmModelInfo>> GetModelsAsync()
    {
        if (_models != null)
        {
            return _models;
        }
        var key = OpenRouter.LoadKey(_settings.OpenRouterConfigPath);
        if (key == null)
        {
            return new List<LlmModelInfo>();
        }
        _models = await OpenRouter.GetModels(key);
        return _models;
    }

    public void Request(List<LlmMessage> messages, string contextDisplay, int count)
    {
        lock (_lock)
        {
            _pending = (messages, contextDisplay, count);
            if (_busy)
            {
                return;
            }
            _busy = true;
        }
        _ = RunWorker();
    }

    private async Task RunWorker()
    {
        while (true)
        {
            (List<LlmMessage> Messages, string ContextDisplay, int Count) job;
            lock (_lock)
            {
                if (_pending is not { } p)
                {
                    _busy = false;
                    return;
                }
                job = p;
                _pending = null;
            }
            LastContext = job.ContextDisplay;
            try
            {
                var key = OpenRouter.LoadKey(_settings.OpenRouterConfigPath);
                if (key == null)
                {
                    continue;
                }
                var pricing = _models?.FirstOrDefault(m => m.Id == _settings.LlmModel);
                int maxTokens = Math.Min(BaseMaxTokens + PerSuggestionMaxTokens * job.Count, MaxTokensCap);
                // Counted at call time, not completion, so the readout reflects in-flight requests immediately.
                _settings.LlmRequestCount++;
                _settings.Save();
                Updated?.Invoke();
                var completion = await OpenRouter.Complete(key, _settings.LlmModel, job.Messages, maxTokens, pricing);
                Suggestions = completion.Lines.Take(job.Count).ToList();
                LastError = "";
                _settings.LlmTotalCostUsd += completion.CostUsd;
                _settings.LlmInputTokens += completion.PromptTokens;
                _settings.LlmOutputTokens += completion.CompletionTokens;
                _settings.LlmTotalLatencyMs += completion.LatencyMs;
                _settings.Save();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                AppLog.Write("llm", "prediction failed: " + ex.Message);
            }
            Updated?.Invoke();
        }
    }
}
