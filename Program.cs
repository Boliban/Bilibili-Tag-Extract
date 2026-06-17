using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VideoTagProcessor
{
    // 原始JSON数据项
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
        public string InputFolder { get; set; } = "";
        public string OutputFolder { get; set; } = "";
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

    // 合并历史记录格式
    public class MergeHistory
    {
        public List<string> MergedFiles { get; set; } = new();
    }

    class Program
    {
        static readonly HttpClient httpClient = new HttpClient();
        static string configFile = "config.json";   // 配置文件路径，默认为当前目录

        static async Task Main(string[] args)
        {
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            if (args.Length == 0)
                await InteractiveMode();
            else
                await CommandLineMode(args);
        }

        // ==================== 交互模式 ====================
        static async Task InteractiveMode()
        {
            var config = LoadConfig(configFile);

            while (true)
            {
                Console.WriteLine("\n请选择要执行的操作：");
                Console.WriteLine("1 - 统计标签/作者并输出 CSV（使用最新历史文件）");
                Console.WriteLine("2 - 获取视频详细标签并输出 JSON（使用最新历史文件）");
                Console.WriteLine("3 - 执行 1 + 2");
                Console.WriteLine("4 - 自动合并所有新历史文件（增量，不获取标签）");
                Console.WriteLine("5 - 自动合并所有新历史文件 + 智能获取详细标签");
                Console.Write("请输入数字 (1/2/3/4/5)：");
                string input = Console.ReadLine()?.Trim();

                switch (input)
                {
                    case "1":
                        await RunStatisticsForLatest(config);
                        return;
                    case "2":
                        await RunFetchTagsForLatest(config);
                        return;
                    case "3":
                        await RunFetchTagsForLatest(config);
                        await RunStatisticsForLatest(config);
                        return;
                    case "4":
                        await AutoMergeFiles(config, applyFetchTags: false);
                        return;
                    case "5":
                        await AutoMergeFiles(config, applyFetchTags: true);
                        return;
                    default:
                        Console.WriteLine("输入无效，请输入 1～5。");
                        break;
                }
            }
        }

        // ==================== 命令行模式（保留兼容） ====================
        static async Task CommandLineMode(string[] args)
        {
            string inputJson = null;
            string outputCsv = null;
            bool fetchTags = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-i": case "--input":
                        if (i + 1 < args.Length) inputJson = args[++i];
                        break;
                    case "-c": case "--config":
                        if (i + 1 < args.Length) configFile = args[++i];
                        break;
                    case "-o": case "--output":
                        if (i + 1 < args.Length) outputCsv = args[++i];
                        break;
                    case "-f": case "--fetch-tags":
                        fetchTags = true;
                        break;
                    default:
                        Console.WriteLine($"未知参数: {args[i]}");
                        return;
                }
            }

            var config = LoadConfig(configFile);

            if (!string.IsNullOrEmpty(inputJson))
            {
                var items = LoadAndFilterItems(inputJson);
                if (fetchTags)
                {
                    string fetchOut = GetOutputPath(config, "input_with_tags.json");
                    await FetchAllTags(items, fetchOut);
                }
                if (outputCsv != null)
                {
                    await RunStatistics(items, config, outputCsv);
                }
                else
                {
                    await RunStatistics(items, config);
                }
            }
            else
            {
                Console.WriteLine("命令行模式需要指定输入文件 (-i)。");
            }
        }

        // ==================== 模式 1/2/3 辅助 ====================
        static async Task RunStatisticsForLatest(Config config)
        {
            string mergedPath = GetOutputPath(config, "merged_history.json");
            if (!File.Exists(mergedPath))
            {
                Console.WriteLine($"合并文件不存在: {mergedPath}，回退到最新历史文件。");
                var latestFile = GetLatestHistoryFile(config);
                if (latestFile == null) return;
                var items = LoadAndFilterItems(latestFile);
                await RunStatistics(items, config);
                return;
            }

            Console.WriteLine($"使用合并文件: {mergedPath}");
            var mergedItems = LoadAndFilterItems(mergedPath);
            await RunStatistics(mergedItems, config);
        }

        static async Task RunFetchTagsForLatest(Config config)
        {
            var latestFile = GetLatestHistoryFile(config);
            if (latestFile == null) return;
            var items = LoadAndFilterItems(latestFile);
            string fetchOut = GetOutputPath(config, "input_with_tags.json");
            await FetchAllTags(items, fetchOut);
        }

        static string GetLatestHistoryFile(Config config)
        {
            var files = ScanHistoryFiles(config);
            if (files.Count == 0)
            {
                Console.WriteLine("输入文件夹中没有找到 bilibili-history-*.json 文件。");
                return null;
            }
            var latest = files.Last();
            Console.WriteLine($"自动选择最新文件: {Path.GetFileName(latest)}");
            return latest;
        }

        // ==================== 自动合并（模式 4/5） ====================
        static async Task AutoMergeFiles(Config config, bool applyFetchTags)
        {
            var allFiles = ScanHistoryFiles(config);
            if (allFiles.Count == 0)
            {
                Console.WriteLine("输入文件夹中没有历史文件可供合并。");
                return;
            }

            string mergeHistoryPath = GetOutputPath(config, "merge_history.json");
            var history = LoadMergeHistory(mergeHistoryPath);
            var newFiles = allFiles.Where(f => !history.MergedFiles.Contains(f)).ToList();

            string outputFileBase = GetOutputPath(config, "merged_history.json");
            string outputFileWithTags = GetOutputPath(config, "merged_history_with_tags.json");

            // 如果没有新文件
            if (newFiles.Count == 0)
            {
                if (applyFetchTags)
                {
                    string sourceFile = File.Exists(outputFileWithTags) ? outputFileWithTags : outputFileBase;
                    if (!File.Exists(sourceFile))
                    {
                        Console.WriteLine("没有可用的合并文件，无法补充标签。");
                        return;
                    }
                    var existingItems = LoadVideoItems(sourceFile);
                    if (existingItems == null || existingItems.Count == 0)
                    {
                        Console.WriteLine("合并文件为空，无法补充标签。");
                        return;
                    }
                    Console.WriteLine("所有历史文件均已合并过，检查现有合并文件中缺失标签的视频...");
                    await FetchMissingTags(existingItems, outputFileWithTags);
                }
                else
                {
                    Console.WriteLine("所有历史文件均已合并过，无需操作。");
                }
                return;
            }

            Console.WriteLine($"发现 {newFiles.Count} 个新文件待合并：");
            foreach (var f in newFiles) Console.WriteLine($"  {Path.GetFileName(f)}");

            // 优先使用带标签的合并文件
            List<VideoItem> baseItems = null;
            if (File.Exists(outputFileWithTags))
            {
                baseItems = LoadVideoItems(outputFileWithTags);
                Console.WriteLine($"已加载带标签的合并文件: {outputFileWithTags} ({baseItems?.Count ?? 0} 条)");
            }
            else if (File.Exists(outputFileBase))
            {
                baseItems = LoadVideoItems(outputFileBase);
                Console.WriteLine($"已加载合并文件（无标签）: {outputFileBase} ({baseItems?.Count ?? 0} 条)");
            }

            var mergedItems = MergeWithBase(baseItems, newFiles);
            mergedItems = mergedItems.OrderByDescending(item => item.ViewAt).ToList();

            SaveItemsToJson(mergedItems, outputFileBase);
            Console.WriteLine($"合并完成，共 {mergedItems.Count} 条记录，已输出到 {outputFileBase}");

            // 更新历史
            foreach (var f in newFiles) history.MergedFiles.Add(f);
            SaveMergeHistory(mergeHistoryPath, history);

            if (applyFetchTags)
            {
                Console.WriteLine("为合并后的视频补充详细标签（跳过已有标签的视频）...");
                await FetchMissingTags(mergedItems, outputFileWithTags);
            }
        }

        static List<VideoItem> MergeWithBase(List<VideoItem> baseItems, List<string> newFilePaths)
        {
            var result = baseItems ?? new List<VideoItem>();
            long maxViewAt = result.Count > 0 ? result.Max(it => it.ViewAt) : 0;
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            foreach (var path in newFilePaths)
            {
                var jsonText = File.ReadAllText(path);
                var items = JsonSerializer.Deserialize<List<VideoItem>>(jsonText, options);
                if (items == null || items.Count == 0) continue;

                var newItems = items.Where(it => it.ViewAt > maxViewAt).ToList();
                if (newItems.Count > 0)
                {
                    result.AddRange(newItems);
                    maxViewAt = Math.Max(maxViewAt, newItems.Max(it => it.ViewAt));
                }
            }
            return result;
        }

        // ==================== 核心统计 ====================
        static async Task RunStatistics(List<VideoItem> items, Config config, string overrideCsvPath = null)
        {
            var itemsWithDate = items.Select(item =>
            {
                DateTime date = DateTimeOffset.FromUnixTimeSeconds(item.ViewAt).LocalDateTime;
                return new { item.TagName, item.AuthorName, YearMonth = date.ToString("yyyy-MM") };
            }).Where(x => !config.ExcludeMonths.Contains(x.YearMonth)).ToList();

            // 检查未映射标签
            var unmappedTags = new HashSet<string>(
                itemsWithDate.Select(x => x.TagName)
                             .Where(t => !config.MergeMapping.ContainsKey(t))
                             .Distinct()
            );
            if (unmappedTags.Count > 0)
            {
                Console.WriteLine("错误：以下标签在 merge_mapping 中没有对应映射，请更新 config.json：");
                foreach (var t in unmappedTags.OrderBy(t => t)) Console.WriteLine($"  - {t}");
                return;
            }

            var mappedItems = itemsWithDate.Select(x => new
            {
                YearMonth = x.YearMonth,
                Tag = config.MergeMapping[x.TagName],
                Author = x.AuthorName
            }).ToList();

            var allMonths = mappedItems.Select(x => x.YearMonth).Distinct().OrderBy(m => m).ToList();

            // 标签矩阵
            var tagMatrix = mappedItems
                .GroupBy(x => new { x.Tag, x.YearMonth })
                .GroupBy(g => g.Key.Tag)
                .ToDictionary(g => g.Key, g => g.ToDictionary(x => x.Key.YearMonth, x => x.Count()));

            var sortedTags = tagMatrix
                .OrderByDescending(kvp => kvp.Value.Values.Sum())
                .ThenBy(kvp => kvp.Key)
                .Select(kvp => kvp.Key)
                .ToList();

            string csvPath = overrideCsvPath ?? GetOutputPath(config, "output.csv");
            WriteTagMatrixCsv(csvPath, sortedTags, allMonths, tagMatrix);
            Console.WriteLine($"标签统计已写入: {csvPath}");

            // 作者矩阵
            var authorMatrix = mappedItems
                .GroupBy(x => new { x.Author, x.YearMonth })
                .GroupBy(g => g.Key.Author)
                .ToDictionary(g => g.Key, g => g.ToDictionary(x => x.Key.YearMonth, x => x.Count()));

            var filteredAuthors = authorMatrix
                .Where(kvp => kvp.Value.Values.Sum() >= config.AuthorMinCount)
                .OrderByDescending(kvp => kvp.Value.Values.Sum())
                .ThenBy(kvp => kvp.Key)
                .Select(kvp => kvp.Key)
                .ToList();

            string authorsCsv = GetOutputPath(config, "output_authors.csv");
            WriteAuthorMatrixCsv(authorsCsv, filteredAuthors, allMonths, authorMatrix);
            Console.WriteLine($"作者统计已写入: {authorsCsv}");
        }

        static void WriteTagMatrixCsv(string path, List<string> tags, List<string> months,
                                      Dictionary<string, Dictionary<string, int>> matrix)
        {
            using var writer = new StreamWriter(path, false, Encoding.UTF8);
            var header = new List<string> { "Tag" };
            header.AddRange(months.Select(EscapeCsvField));
            writer.WriteLine(string.Join(",", header));

            foreach (var tag in tags)
            {
                var row = new List<string> { EscapeCsvField(tag) };
                var tagData = matrix.TryGetValue(tag, out var dict) ? dict : new Dictionary<string, int>();
                foreach (var month in months)
                    row.Add(tagData.TryGetValue(month, out int c) ? c.ToString() : "0");
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
                var authorData = matrix.TryGetValue(author, out var dict) ? dict : new Dictionary<string, int>();
                foreach (var month in months)
                    row.Add(authorData.TryGetValue(month, out int c) ? c.ToString() : "0");
                writer.WriteLine(string.Join(",", row));
            }
        }

        // ==================== 标签抓取 ====================
        static async Task FetchAllTags(List<VideoItem> items, string outputPath)
        {
            await FetchTagsInternal(items, outputPath, onlyMissing: false);
        }

        static async Task FetchMissingTags(List<VideoItem> items, string outputPath)
        {
            await FetchTagsInternal(items, outputPath, onlyMissing: true);
        }

        static async Task FetchTagsInternal(List<VideoItem> items, string outputPath, bool onlyMissing)
        {
            var toFetch = onlyMissing
                ? items.Where(item =>
                    (item.DetailTags == null || item.DetailTags.Count == 0) &&
                    item.Business == "archive")
                .ToList()
                : items.Where(item => item.Business == "archive").ToList();

            if (toFetch.Count == 0)
            {
                Console.WriteLine("所有 archive 视频均已包含详细标签，无需获取。");
                SaveItemsToJson(items, outputPath);
                return;
            }

            Console.WriteLine($"需要获取标签的 archive 视频：{toFetch.Count} / {items.Count} 条");
            int successCount = 0;
            var rng = new Random();
            for (int i = 0; i < toFetch.Count; i++)
            {
                var item = toFetch[i];
                Console.Write($"\r处理 {i + 1}/{toFetch.Count} (BV:{item.Bvid})");
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
                await Task.Delay(rng.Next(150, 400));
            }
            Console.WriteLine($"\n标签抓取完成，成功 {successCount}/{toFetch.Count} 条。");
            SaveItemsToJson(items, outputPath);
        }

        static async Task<List<BiliTag>> FetchVideoTags(string bvid)
        {
            string url = $"https://api.bilibili.com/x/tag/archive/tags?bvid={bvid}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Referer", "https://www.bilibili.com/");

            var response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            var root = doc.RootElement;

            if (root.GetProperty("code").GetInt32() != 0)
                return new List<BiliTag>();

            var data = root.GetProperty("data");
            if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() == 0)
                return new List<BiliTag>();

            return JsonSerializer.Deserialize<List<BiliTag>>(data.GetRawText()) ?? new List<BiliTag>();
        }

        static void SaveItemsToJson(List<VideoItem> items, string path)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            string json = JsonSerializer.Serialize(items, options);
            File.WriteAllText(path, json);
            Console.WriteLine($"已输出文件: {path}");
        }

        // ==================== 文件扫描与历史管理 ====================
        static List<string> ScanHistoryFiles(Config config)
        {
            string dir = string.IsNullOrEmpty(config.InputFolder) ? "." : config.InputFolder;
            if (!Directory.Exists(dir)) return new List<string>();

            var files = Directory.GetFiles(dir, "bilibili-history-*.json")
                .Where(f => Regex.IsMatch(Path.GetFileName(f),
                    @"^bilibili-history-(\d{4}-\d{2}-\d{2})( \(\d+\))?\.json$"))
                .ToList();

            files.Sort((a, b) =>
            {
                var da = ExtractDateAndNumber(a);
                var db = ExtractDateAndNumber(b);
                int dateCmp = da.Date.CompareTo(db.Date);
                if (dateCmp != 0) return dateCmp;
                return da.Number.CompareTo(db.Number);
            });
            return files;
        }

        static (DateTime Date, int Number) ExtractDateAndNumber(string filePath)
        {
            var name = Path.GetFileNameWithoutExtension(filePath);
            var match = Regex.Match(name, @"^bilibili-history-(\d{4}-\d{2}-\d{2})( \((\d+)\))?$");
            if (!match.Success)
                throw new FormatException($"文件名格式错误: {name}");

            var date = DateTime.ParseExact(match.Groups[1].Value, "yyyy-MM-dd", null);
            int number = 0;
            if (match.Groups[3].Success)
                number = int.Parse(match.Groups[3].Value);
            return (date, number);
        }

        static MergeHistory LoadMergeHistory(string path)
        {
            if (!File.Exists(path)) return new MergeHistory();
            try
            {
                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<MergeHistory>(json) ?? new MergeHistory();
            }
            catch { return new MergeHistory(); }
        }

        static void SaveMergeHistory(string path, MergeHistory history)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(history, options);
            File.WriteAllText(path, json);
        }

        // ==================== 基础 IO ====================
        static List<VideoItem> LoadAndFilterItems(string filePath)
        {
            if (!File.Exists(filePath))
            {
                Console.WriteLine($"文件不存在: {filePath}");
                Environment.Exit(1);
            }

            var jsonText = File.ReadAllText(filePath);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var items = JsonSerializer.Deserialize<List<VideoItem>>(jsonText, options);
            items = items?.Where(item =>
                !string.IsNullOrEmpty(item.TagName) &&
                !string.IsNullOrEmpty(item.AuthorName)
            ).ToList();

            if (items == null || items.Count == 0)
            {
                Console.WriteLine("输入数据为空（或过滤后无有效数据）。");
                Environment.Exit(1);
            }
            return items;
        }

        static List<VideoItem> ?LoadVideoItems(string filePath)
        {
            if (!File.Exists(filePath)) return null;
            var jsonText = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<List<VideoItem>>(jsonText,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }

        static Config LoadConfig(string path)
        {
            if (!File.Exists(path))
            {
                Console.WriteLine($"配置文件 {path} 未找到，使用默认配置。");
                return new Config();
            }

            var json = File.ReadAllText(path);
            var config = new Config();
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("merge_mapping", out var mergeEl))
                config.MergeMapping = JsonSerializer.Deserialize<Dictionary<string, string>>(mergeEl.GetRawText()) ?? new();
            if (doc.RootElement.TryGetProperty("exclude_months", out var exclEl))
                config.ExcludeMonths = JsonSerializer.Deserialize<List<string>>(exclEl.GetRawText()) ?? new();
            if (doc.RootElement.TryGetProperty("author_min_count", out var amcEl))
                config.AuthorMinCount = amcEl.GetInt32();
            if (doc.RootElement.TryGetProperty("input_folder", out var inEl))
                config.InputFolder = inEl.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("output_folder", out var outEl))
                config.OutputFolder = outEl.GetString() ?? "";

            return config;
        }

        static string GetOutputPath(Config config, string filename)
        {
            string baseDir = string.IsNullOrEmpty(config.OutputFolder) ? "." : config.OutputFolder;
            if (!Directory.Exists(baseDir)) Directory.CreateDirectory(baseDir);
            return Path.Combine(baseDir, filename);
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