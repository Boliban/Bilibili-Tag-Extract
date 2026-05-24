using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoTagProcessor
{
    // 原始JSON数据项
    public class VideoItem
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("business")]
        public string Business { get; set; }

        [JsonPropertyName("bvid")]
        public string Bvid { get; set; }

        [JsonPropertyName("cid")]
        public long Cid { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("tag_name")]
        public string TagName { get; set; }

        [JsonPropertyName("cover")]
        public string Cover { get; set; }

        [JsonPropertyName("view_at")]
        public long ViewAt { get; set; } // 秒级时间戳

        [JsonPropertyName("uri")]
        public string Uri { get; set; }

        [JsonPropertyName("author_name")]
        public string AuthorName { get; set; }

        [JsonPropertyName("author_mid")]
        [JsonConverter(typeof(AutoStringConverter))]
        public string AuthorMid { get; set; }

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
    }

    // 配置文件结构（仅保留用到的字段）
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

    // 自动类型转换器：数字或字符串 → 字符串
    public class AutoStringConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
                return reader.GetString();

            if (reader.TokenType == JsonTokenType.Number)
            {
                using var doc = JsonDocument.ParseValue(ref reader);
                return doc.RootElement.GetRawText();
            }

            using var docFallback = JsonDocument.ParseValue(ref reader);
            return docFallback.RootElement.GetRawText();
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value);
        }
    }

    class Program
    {
        // 作者出现次数最低阈值
        const int AuthorMinCount = 5;

        static void Main(string[] args)
        {
            string inputJson = "input.json";
            string configFile = "config.json";
            string outputCsv = "output.csv";

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
                    default:
                        Console.WriteLine($"未知参数: {args[i]}");
                        Console.WriteLine("用法: app [-i input.json] [-c config.json] [-o output.csv]");
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
                List<VideoItem>? items = JsonSerializer.Deserialize<List<VideoItem>>(jsonText, options);

                // 过滤掉 tag_name 或 author_name 为空的数据
                items = items?.Where(item =>
                    !string.IsNullOrEmpty(item.TagName) &&
                    !string.IsNullOrEmpty(item.AuthorName)
                ).ToList();

                if (items == null || items.Count == 0)
                {
                    Console.WriteLine("输入数据为空。");
                    return;
                }

                Config config = LoadConfig(configFile);

                // 转换时间戳，提取年月，排除指定月份
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

                // 标签映射（唯一一次合并）
                var mappedItems = itemsWithDate.Select(x =>
                {
                    string newTag = config.MergeMapping[x.TagName]; // 已确保存在
                    return new { YearMonth = x.YearMonth, Tag = newTag, Author = x.AuthorName };
                }).ToList();

                // === 标签统计 CSV ===
                var tagRows = mappedItems
                    .GroupBy(x => new { x.YearMonth, x.Tag })
                    .Select(g => new CsvRow
                    {
                        Month = g.Key.YearMonth,
                        Tag = g.Key.Tag,
                        Count = g.Count()
                    })
                    .OrderBy(row => row.Month)
                    .ThenByDescending(row => row.Count)
                    .ThenBy(row => row.Tag)
                    .ToList();

                WriteCsv(outputCsv, tagRows);
                Console.WriteLine($"标签统计已写入: {outputCsv}");

                // === 作者统计 CSV（按月，过滤出现次数 < AuthorMinCount） ===
                var authorRows = mappedItems
                    .GroupBy(x => new { x.YearMonth, x.Author })
                    .Select(g => new MonthlyAuthorCount
                    {
                        Month = g.Key.YearMonth,
                        Author = g.Key.Author,
                        Count = g.Count()
                    })
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

        static Config LoadConfig(string path)
        {
            if (!File.Exists(path))
            {
                Console.WriteLine($"配置文件 {path} 未找到，使用默认配置（无合并、无排除月份）。");
                return new Config();
            }

            string configJson = File.ReadAllText(path);
            var config = new Config();

            using (JsonDocument doc = JsonDocument.Parse(configJson))
            {
                if (doc.RootElement.TryGetProperty("merge_mapping", out JsonElement mergeElement))
                    config.MergeMapping = JsonSerializer.Deserialize<Dictionary<string, string>>(mergeElement.GetRawText()) ?? new();

                if (doc.RootElement.TryGetProperty("exclude_months", out JsonElement excludeElement))
                    config.ExcludeMonths = JsonSerializer.Deserialize<List<string>>(excludeElement.GetRawText()) ?? new();
            }

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