using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoTagProcessor
{
    // 原始 JSON 数据项（新增 detail_tags）
    public class VideoItem
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("business")] public string Business { get; set; }
        [JsonPropertyName("bvid")] public string Bvid { get; set; }
        [JsonPropertyName("cid")] public long Cid { get; set; }
        [JsonPropertyName("title")] public string Title { get; set; }
        [JsonPropertyName("tag_name")] public string TagName { get; set; }
        [JsonPropertyName("cover")] public string Cover { get; set; }
        [JsonPropertyName("view_at")] public long ViewAt { get; set; }
        [JsonPropertyName("uri")] public string Uri { get; set; }
        [JsonPropertyName("author_name")] public string AuthorName { get; set; }

        [JsonPropertyName("author_mid")]
        [JsonConverter(typeof(AutoStringConverter))]
        public string AuthorMid { get; set; }

        [JsonPropertyName("progress")] public int Progress { get; set; }
        [JsonPropertyName("duration")] public int Duration { get; set; }
        [JsonPropertyName("is_fav")] public bool IsFav { get; set; }
        [JsonPropertyName("timestamp")] public long Timestamp { get; set; }
        [JsonPropertyName("uploaded")] public bool Uploaded { get; set; }

        // 新字段：详细标签（仅当启用抓取且有值时输出）
        [JsonPropertyName("detail_tags")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<BiliTag> DetailTags { get; set; }
    }

    // B 站标签结构
    public class BiliTag
    {
        [JsonPropertyName("tag_id")] public long TagId { get; set; }
        [JsonPropertyName("tag_name")] public string TagName { get; set; }
    }

    // 配置文件结构（保持不变）
    public class Config
    {
        public Dictionary<string, string> MergeMapping { get; set; } = new();
        public List<string> ExcludeMonths { get; set; } = new();
    }

    // 标签统计输出行
    public class CsvRow
    {
        public string Month { get; set; }
        public string Tag { get; set; }
        public int Count { get; set; }
    }

    // 作者统计输出行（按月）
    public class MonthlyAuthorCount
    {
        public string Month { get; set; }
        public string Author { get; set; }
        public int Count { get; set; }
    }

    // 自动类型转换器
    public class AutoStringConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String) return reader.GetString();
            if (reader.TokenType == JsonTokenType.Number)
            {
                using var doc = JsonDocument.ParseValue(ref reader);
                return doc.RootElement.GetRawText();
            }
            using var docFallback = JsonDocument.ParseValue(ref reader);
            return docFallback.RootElement.GetRawText();
        }
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
            => writer.WriteStringValue(value);
    }

    class Program
    {
        const int AuthorMinCount = 5;          // 作者月度最低出现次数
        static readonly HttpClient httpClient = new HttpClient();

        static async Task Main(string[] args)
        {
            // 设置基础请求头（添加 Referer 在此处也可，但上方代码已单独加）
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            string inputJson = "input.json";
            string configFile = "config.json";
            string outputCsv = "output.csv";
            bool fetchTags = false;
            string fetchOutput = "input_with_tags.json";   // 抓取后输出文件

            // 解析命令行参数
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-i":
                    case "--input":
                        if (i + 1 < args.Length) inputJson = args[++i];
                        break;
                    case "-c":
                    case "--config":
                        if (i + 1 < args.Length) configFile = args[++i];
                        break;
                    case "-o":
                    case "--output":
                        if (i + 1 < args.Length) outputCsv = args[++i];
                        break;
                    case "-f":
                    case "--fetch-tags":
                        fetchTags = true;
                        break;
                    case "--fetch-output":
                        if (i + 1 < args.Length) fetchOutput = args[++i];
                        break;
                    default:
                        Console.WriteLine($"未知参数: {args[i]}");
                        return;
                }
            }

            try
            {
                if (!File.Exists(inputJson))
                {
                    Console.WriteLine($"输入文件不存在: {inputJson}");
                    return;
                }

                string jsonText = File.ReadAllText(inputJson);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                List<VideoItem> ?items = JsonSerializer.Deserialize<List<VideoItem>>(jsonText, options);

                // 过滤空标签/作者
                items = items?.Where(item =>
                    !string.IsNullOrEmpty(item.TagName) &&
                    !string.IsNullOrEmpty(item.AuthorName)
                ).ToList();

                if (items == null || items.Count == 0)
                {
                    Console.WriteLine("输入数据为空（或过滤后无有效数据）。");
                    return;
                }

                Config config = LoadConfig(configFile);

                // 转换时间戳，排除月份
                var itemsWithDate = items.Select(item =>
                {
                    DateTime date = DateTimeOffset.FromUnixTimeSeconds(item.ViewAt).LocalDateTime;
                    return new
                    {
                        item.TagName,
                        item.AuthorName,
                        YearMonth = date.ToString("yyyy-MM")
                    };
                }).Where(x => !config.ExcludeMonths.Contains(x.YearMonth)).ToList();

                // 检查未映射标签
                var unmappedTags = new HashSet<string>(
                    itemsWithDate.Select(x => x.TagName)
                                 .Where(tag => !config.MergeMapping.ContainsKey(tag))
                                 .Distinct()
                );
                if (unmappedTags.Count > 0)
                {
                    Console.WriteLine("错误：以下标签在 merge_mapping 中没有对应映射，请更新 config.json：");
                    foreach (var tag in unmappedTags.OrderBy(t => t))
                        Console.WriteLine($"  - {tag}");
                    Console.WriteLine("程序停止。");
                    return;
                }

                // ----- 新增：标签抓取（可选）-----
                if (fetchTags)
                {
                    Console.WriteLine($"开始获取 {items.Count} 条视频的详细标签...");
                    int successCount = 0;
                    for (int i = 0; i < items.Count; i++)
                    {
                        var item = items[i];
                        Console.Write($"\r处理 {i + 1}/{items.Count} (BV:{item.Bvid})");
                        try
                        {
                            item.DetailTags = await FetchVideoTags(item.Bvid);
                            successCount++;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"\n获取 BV:{item.Bvid} 标签失败: {ex.Message}");
                            item.DetailTags = null;   // 失败时置空
                        }
                        // 延时，避免请求过快被限制
                        await Task.Delay(150);
                    }
                    Console.WriteLine($"\n标签抓取完成，成功 {successCount}/{items.Count} 条。");

                    // 输出增强后的 JSON 文件
                    var jsonOptions = new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    };
                    string enrichedJson = JsonSerializer.Serialize(items, jsonOptions);
                    File.WriteAllText(fetchOutput, enrichedJson);
                    Console.WriteLine($"已输出带详细标签的文件: {fetchOutput}");
                }
                // --------------------------------

                // 原有统计流程
                var mappedItems = itemsWithDate.Select(x =>
                {
                    string newTag = config.MergeMapping[x.TagName];
                    return new { YearMonth = x.YearMonth, Tag = newTag, Author = x.AuthorName };
                }).ToList();

                // 标签月度统计
                var tagRows = mappedItems
                    .GroupBy(x => new { x.YearMonth, x.Tag })
                    .Select(g => new CsvRow { Month = g.Key.YearMonth, Tag = g.Key.Tag, Count = g.Count() })
                    .OrderBy(row => row.Month)
                    .ThenByDescending(row => row.Count)
                    .ThenBy(row => row.Tag)
                    .ToList();

                WriteCsv(outputCsv, tagRows);
                Console.WriteLine($"标签统计已写入: {outputCsv}");

                // 作者月度统计
                var authorRows = mappedItems
                    .GroupBy(x => new { x.YearMonth, x.Author })
                    .Select(g => new MonthlyAuthorCount { Month = g.Key.YearMonth, Author = g.Key.Author, Count = g.Count() })
                    .Where(row => row.Count >= AuthorMinCount)
                    .OrderBy(row => row.Month)
                    .ThenByDescending(row => row.Count)
                    .ThenBy(row => row.Author)
                    .ToList();

                string outputDir = Path.GetDirectoryName(outputCsv);
                string baseName = Path.GetFileNameWithoutExtension(outputCsv);
                string authorsCsv = Path.Combine(outputDir ?? "", $"{baseName}_authors.csv");
                WriteAuthorsCsv(authorsCsv, authorRows);
                Console.WriteLine($"作者统计已写入: {authorsCsv}");

                Console.WriteLine("处理完成。");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"发生错误: {ex.Message}");
            }
        }

        // 通过 BV 号获取视频详细标签
        static async Task<List<BiliTag>> FetchVideoTags(string bvid)
        {
            string url = $"https://api.bilibili.com/x/tag/archive/tags?bvid={bvid}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // 必须加上 Referer，否则可能返回 -403
            request.Headers.Add("Referer", "https://www.bilibili.com/");

            var response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            using JsonDocument doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            var root = doc.RootElement;

            // 这个接口的返回格式：{ "code":0, "data": [ { "tag_id":123, "tag_name":"xxx" }, ... ] }
            if (root.GetProperty("code").GetInt32() != 0)
            {
                // 非 0 表示请求失败（如稿件失效），返回空列表
                return new List<BiliTag>();
            }

            var data = root.GetProperty("data");
            // 如果 data 本身就是空数组，直接返回空列表
            if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() == 0)
                return new List<BiliTag>();

            return JsonSerializer.Deserialize<List<BiliTag>>(data.GetRawText()) ?? new List<BiliTag>();
        }

        static Config LoadConfig(string path)
        {
            if (!File.Exists(path))
            {
                Console.WriteLine($"配置文件 {path} 未找到，使用默认配置。");
                return new Config();
            }

            string configJson = File.ReadAllText(path);
            var config = new Config();

            using JsonDocument doc = JsonDocument.Parse(configJson);
            if (doc.RootElement.TryGetProperty("merge_mapping", out var mergeEl))
                config.MergeMapping = JsonSerializer.Deserialize<Dictionary<string, string>>(mergeEl.GetRawText()) ?? new();
            if (doc.RootElement.TryGetProperty("exclude_months", out var excludeEl))
                config.ExcludeMonths = JsonSerializer.Deserialize<List<string>>(excludeEl.GetRawText()) ?? new();

            return config;
        }

        static void WriteCsv(string path, List<CsvRow> rows)
        {
            using var writer = new StreamWriter(path, false, Encoding.UTF8);
            writer.WriteLine("Month,Tag,Count");
            foreach (var row in rows)
                writer.WriteLine($"{EscapeCsvField(row.Month)},{EscapeCsvField(row.Tag)},{row.Count}");
        }

        static void WriteAuthorsCsv(string path, List<MonthlyAuthorCount> rows)
        {
            using var writer = new StreamWriter(path, false, Encoding.UTF8);
            writer.WriteLine("Month,Author,Count");
            foreach (var row in rows)
                writer.WriteLine($"{EscapeCsvField(row.Month)},{EscapeCsvField(row.Author)},{row.Count}");
        }

        static string EscapeCsvField(string field)
        {
            if (string.IsNullOrEmpty(field)) return "";
            if (field.Contains(',') || field.Contains('"') || field.Contains('\n'))
                return $"\"{field.Replace("\"", "\"\"")}\"";
            return field;
        }
    }
}