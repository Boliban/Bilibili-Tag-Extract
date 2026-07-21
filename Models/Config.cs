using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VideoTagProcessor;

public class Config
{
    public Dictionary<string, List<string>> MergeMapping { get; set; } = new();
    public List<string> ExcludeMonths { get; set; } = new();
    public int AuthorMinCount { get; set; } = 5;
    public string InputFolder { get; set; } = "";
    public string OutputFolder { get; set; } = "";
    public ClassificationConfig Classification { get; set; } = new();
}

public class ClassificationConfig
{
    public string Provider { get; set; } = "ollama";

    [JsonPropertyName("ollama_url")]
    public string OllamaUrl { get; set; } = "http://localhost:11434";

    [JsonPropertyName("ollama_model")]
    public string OllamaModel { get; set; } = "qwen2.5:3b";

    [JsonPropertyName("deepseek_api_key")]
    public string DeepSeekApiKey { get; set; } = "";

    [JsonPropertyName("deepseek_model")]
    public string DeepSeekModel { get; set; } = "deepseek-chat";

    public List<string> Categories { get; set; } = new();

    [JsonPropertyName("batch_size")]
    public int BatchSize { get; set; } = 10;

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; } = 0.1;

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 1024;

    [JsonPropertyName("max_concurrency")]
    public int MaxConcurrency { get; set; } = 3;

    public bool EnableWarmup { get; set; } = true;
    public int WarmupWaitSeconds { get; set; } = 3;

    [JsonPropertyName("enable_pre_classify")]
    public bool EnablePreClassify { get; set; } = true;

    [JsonPropertyName("pre_classify_rules")]
    public Dictionary<string, PreClassifyRule> PreClassifyRules { get; set; } = new();
}

public class PreClassifyRule
{
    public List<string> Keywords { get; set; } = new();

    [JsonPropertyName("fields")]
    public List<string> Fields { get; set; } = new();

    [JsonPropertyName("require_all_fields")]
    public bool RequireAllFields { get; set; } = false;
}