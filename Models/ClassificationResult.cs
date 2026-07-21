using System.Text.Json.Serialization;

namespace VideoTagProcessor;

public class ClassificationResult
{
    [JsonPropertyName("results")]
    public List<ClassificationItem>? Results { get; set; }
}