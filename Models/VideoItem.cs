using System.Text.Json.Serialization;

namespace VideoTagProcessor;

public class VideoItem
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("business")]
    public string? Business { get; set; }

    [JsonPropertyName("bvid")]
    public string? Bvid { get; set; }

    [JsonPropertyName("cid")]
    public long Cid { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("cover")]
    public string? Cover { get; set; }

    [JsonPropertyName("view_at")]
    public long ViewAt { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("author_name")]
    public string? AuthorName { get; set; }

    [JsonPropertyName("author_mid")]
    [JsonConverter(typeof(AutoStringConverter))]
    public string? AuthorMid { get; set; }

    [JsonPropertyName("progress")]
    public int Progress { get; set; }

    [JsonPropertyName("duration")]
    public int Duration { get; set; }

    [JsonPropertyName("is_fav")]
    public bool IsFav { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("uploaded")]
    public bool Uploaded { get; set; }

    [JsonPropertyName("detail_tags")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<BiliTag>? DetailTags { get; set; }
}