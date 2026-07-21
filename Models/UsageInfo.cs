namespace VideoTagProcessor;

public class UsageInfo
{
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
    public int? PromptCacheHitTokens { get; set; }
    public int? PromptCacheMissTokens { get; set; }
}