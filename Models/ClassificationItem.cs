using System.Text.Json.Serialization;

namespace VideoTagProcessor;

public class ClassificationItem
{
    [JsonPropertyName("i")]
    public int I { get; set; }

    [JsonPropertyName("c")]
    public string? C { get; set; }
}