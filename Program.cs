using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VideoTagProcessor.VideoTagProcessor;

namespace VideoTagProcessor
{
    namespace VideoTagProcessor
    {
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

            [JsonPropertyName("detail_tags")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public List<BiliTag> DetailTags { get; set; }
        }

        public class BiliTag
        {
            [JsonPropertyName("tag_id")] public long TagId { get; set; }
            [JsonPropertyName("tag_name")] public string TagName { get; set; }
        }

        public class Config
        {
            public Dictionary<string, string> MergeMapping { get; set; } = new();
            public List<string> ExcludeMonths { get; set; } = new();
            public int AuthorMinCount { get; set; } = 5;
        }

    }

    public class CsvRow
    {
        public string Month { get; set; }
        public string Tag { get; set; }
        public int Count { get; set; }
    }

    public class MonthlyAuthorCount
    {
        public string Month { get; set; }
        public string Author { get; set; }
        public int Count { get; set; }
    }

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
        // 移除 const int AuthorMinCount
        static readonly HttpClient httpClient = new HttpClient();

        static string inputJson = "input.json";
        static string configFile = "config.json";
        static string outputCsv = "output.csv";
        static string fetchOutput = "input_with_tags.json";

        static async Task Main(string[] args)
        {
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            if (args.Length == 0)
                await InteractiveMode();
            else
                await CommandLineMode(args);
        }

        // ============= 交互模式 =============
        static async Task InteractiveMode()
        {
            var items = LoadAndFilterItems();
            var config = LoadConfig(configFile);

            while (true)
            {
                Console.WriteLine("\n请选择要执行的操作：");
                Console.WriteLine("1 - 仅统计标签/作者并输出 CSV");
                Console.WriteLine("2 - 仅获取视频详细标签并输出 JSON");
                Console.WriteLine("3 - 执行全部（1 + 2）");
                Console.Write("请输入数字 (1/2/3)：");
                string input = Console.ReadLine()?.Trim();

                switch (input)
                {
                    case "1":
                        await RunStatistics(items, config);
                        return;
                    case "2":
                        await RunFetchTags(items);
                        return;
                    case "3":
                        await RunFetchTags(items);
                        await RunStatistics(items, config);
                        return;
                    default:
                        Console.WriteLine("输入无效，请输入 1、2 或 3。");
                        break;
                }
            }
        }

        // ============= 命令行模式 =============
        static async Task CommandLineMode(string[] args)
        {
            bool fetchTags = false;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-i": case "--input": if (i + 1 < args.Length) inputJson = args[++i]; break;
                    case "-c": case "--config": if (i + 1 < args.Length) configFile = args[++i]; break;
                    case "-o": case "--output": if (i + 1 < args.Length) outputCsv = args[++i]; break;
                    case "-f": case "--fetch-tags": fetchTags = true; break;
                    case "--fetch-output": if (i + 1 < args.Length) fetchOutput = args[++i]; break;
                    default: Console.WriteLine($"未知参数: {args[i]}"); return;
                }
            }

            var items = LoadAndFilterItems();
            var config = LoadConfig(configFile);

            if (fetchTags) await RunFetchTags(items);
            await RunStatistics(items, config);
        }

        // ============= 加载与过滤（不变） =============
        static List<VideoItem> LoadAndFilterItems()
        {
            if (!File.Exists(inputJson))
            { Console.WriteLine($"输入文件不存在: {inputJson}"); Environment.Exit(1); }

            string jsonText = File.ReadAllText(inputJson);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            List<VideoItem> ?items = JsonSerializer.Deserialize<List<VideoItem>>(jsonText, options);

            items = items?.Where(item =>
                !string.IsNullOrEmpty(item.TagName) &&
                !string.IsNullOrEmpty(item.AuthorName)
            ).ToList();

            if (items == null || items.Count == 0)
            { Console.WriteLine("输入数据为空（或过滤后无有效数据）。"); Environment.Exit(1); }

            return items;
        }

        // ============= 核心统计（矩阵输出） =============
        static async Task RunStatistics(List<VideoItem> items, Config config)
        {
            // 1. 转换时间戳，排除月份
            var itemsWithDate = items.Select(item =>
            {
                DateTime date = DateTimeOffset.FromUnixTimeSeconds(item.ViewAt).LocalDateTime;
                return new { item.TagName, item.AuthorName, YearMonth = date.ToString("yyyy-MM") };
            }).Where(x => !config.ExcludeMonths.Contains(x.YearMonth)).ToList();

            // 2. 检查未映射标签
            var unmappedTags = new HashSet<string>(
                itemsWithDate.Select(x => x.TagName).Where(t => !config.MergeMapping.ContainsKey(t)).Distinct()
            );
            if (unmappedTags.Count > 0)
            {
                Console.WriteLine("错误：以下标签在 merge_mapping 中没有对应映射，请更新 config.json：");
                foreach (var t in unmappedTags.OrderBy(t => t)) Console.WriteLine($"  - {t}");
                return;
            }

            // 3. 标签映射
            var mappedItems = itemsWithDate.Select(x => new
            {
                YearMonth = x.YearMonth,
                Tag = config.MergeMapping[x.TagName],
                Author = x.AuthorName
            }).ToList();

            // 收集所有月份（按时间排序）
            var allMonths = mappedItems.Select(x => x.YearMonth).Distinct().OrderBy(m => m).ToList();

            // ===== 标签矩阵 =====
            var tagMatrix = mappedItems
                .GroupBy(x => new { x.Tag, x.YearMonth })
                .GroupBy(g => g.Key.Tag)
                .ToDictionary(
                    tagGroup => tagGroup.Key,
                    tagGroup => tagGroup.ToDictionary(g => g.Key.YearMonth, g => g.Count())
                );

            // 标签按总出现次数降序，次数相同按名称升序
            var sortedTags = tagMatrix
                .OrderByDescending(kvp => kvp.Value.Values.Sum())
                .ThenBy(kvp => kvp.Key)
                .Select(kvp => kvp.Key)
                .ToList();

            WriteTagMatrixCsv(outputCsv, sortedTags, allMonths, tagMatrix);

            // ===== 作者矩阵 =====
            var authorMatrix = mappedItems
                .GroupBy(x => new { x.Author, x.YearMonth })
                .GroupBy(g => g.Key.Author)
                .ToDictionary(
                    authorGroup => authorGroup.Key,
                    authorGroup => authorGroup.ToDictionary(g => g.Key.YearMonth, g => g.Count())
                );

            // 筛选总出现次数 >= config.AuthorMinCount 的作者，并按总次数降序排列
            var filteredAuthors = authorMatrix
                .Where(kvp => kvp.Value.Values.Sum() >= config.AuthorMinCount)
                .OrderByDescending(kvp => kvp.Value.Values.Sum())
                .ThenBy(kvp => kvp.Key)
                .Select(kvp => kvp.Key)
                .ToList();

            string outputDir = Path.GetDirectoryName(outputCsv);
            string baseName = Path.GetFileNameWithoutExtension(outputCsv);
            string authorsCsv = Path.Combine(outputDir ?? "", $"{baseName}_authors.csv");
            WriteAuthorMatrixCsv(authorsCsv, filteredAuthors, allMonths, authorMatrix);

            Console.WriteLine($"标签统计已写入: {outputCsv}");
            Console.WriteLine($"作者统计已写入: {authorsCsv}");
            Console.WriteLine("统计完成。");
        }

        // ========== 矩阵输出函数 ==========
        static void WriteTagMatrixCsv(string path, List<string> tags, List<string> months,
                                      Dictionary<string, Dictionary<string, int>> matrix)
        {
            using var writer = new StreamWriter(path, false, Encoding.UTF8);
            // 表头
            var header = new List<string> { "Tag" };
            header.AddRange(months.Select(EscapeCsvField));
            writer.WriteLine(string.Join(",", header));

            foreach (var tag in tags)
            {
                var row = new List<string> { EscapeCsvField(tag) };
                var tagData = matrix.ContainsKey(tag) ? matrix[tag] : new Dictionary<string, int>();
                foreach (var month in months)
                {
                    int count = tagData.TryGetValue(month, out int c) ? c : 0;
                    row.Add(count.ToString());
                }
                writer.WriteLine(string.Join(",", row));
            }
        }

        static void WriteAuthorMatrixCsv(string path, List<string> authors, List<string> months,
                                         Dictionary<string, Dictionary<string, int>> matrix)
        {
            using var writer = new StreamWriter(path, false, Encoding.UTF8);
            var header = new List<string> { "Author" };
            header.AddRange(months.Select(EscapeCsvField));
            writer.WriteLine(string.Join(",", header));

            foreach (var author in authors)
            {
                var row = new List<string> { EscapeCsvField(author) };
                var authorData = matrix.ContainsKey(author) ? matrix[author] : new Dictionary<string, int>();
                foreach (var month in months)
                {
                    int count = authorData.TryGetValue(month, out int c) ? c : 0;
                    row.Add(count.ToString());
                }
                writer.WriteLine(string.Join(",", row));
            }
        }

        // ============= 标签抓取 =============
        static async Task RunFetchTags(List<VideoItem> items)
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
                    item.DetailTags = null;
                }
                await Task.Delay(150);
            }
            Console.WriteLine($"\n标签抓取完成，成功 {successCount}/{items.Count} 条。");

            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            string enrichedJson = JsonSerializer.Serialize(items, jsonOptions);
            File.WriteAllText(fetchOutput, enrichedJson);
            Console.WriteLine($"已输出带详细标签的文件: {fetchOutput}");
        }

        // ============= 获取视频标签 API =============
        static async Task<List<BiliTag>> FetchVideoTags(string bvid)
        {
            string url = $"https://api.bilibili.com/x/tag/archive/tags?bvid={bvid}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Referer", "https://www.bilibili.com/");

            var response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            using JsonDocument doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            var root = doc.RootElement;

            if (root.GetProperty("code").GetInt32() != 0)
                return new List<BiliTag>();

            var data = root.GetProperty("data");
            if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() == 0)
                return new List<BiliTag>();

            return JsonSerializer.Deserialize<List<BiliTag>>(data.GetRawText()) ?? new List<BiliTag>();
        }

        // ============= 配置文件加载（增加 author_min_count） =============
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
            if (doc.RootElement.TryGetProperty("author_min_count", out var authorMinEl))
                config.AuthorMinCount = authorMinEl.GetInt32();

            return config;
        }

        // ========== 原有辅助函数（EscapeCsvField）保留 ==========
        static string EscapeCsvField(string field)
        {
            if (string.IsNullOrEmpty(field)) return "";
            if (field.Contains(',') || field.Contains('"') || field.Contains('\n'))
                return $"\"{field.Replace("\"", "\"\"")}\"";
            return field;
        }
    }
}