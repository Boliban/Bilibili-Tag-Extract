using System.Text.Json.Serialization;

namespace VideoTagProcessor;

public class BiliTag
{
    [JsonPropertyName("tag_id")]
    public long TagId { get; set; }

    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }
}