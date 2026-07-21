using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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

        // 预分类开关
        public bool EnablePreClassify { get; set; } = true;

        [JsonPropertyName("pre_classify_rules")]
        public Dictionary<string, PreClassifyRule> PreClassifyRules { get; set; } = new();
    }

    public class PreClassifyRule
    {
        public List<string> Keywords { get; set; } = new();
        public List<string> Fields { get; set; } = new(); // 为空则默认全部
        public bool RequireAllFields { get; set; } = false;
    }

    public class UsageInfo
    {
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int TotalTokens { get; set; }
        public int? PromptCacheHitTokens { get; set; }
        public int? PromptCacheMissTokens { get; set; }
    }

    public class ClassificationResponse
    {
        public Dictionary<string, string> Results { get; set; }
        public UsageInfo Usage { get; set; }
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

    public class ClassificationItem
    {
        [JsonPropertyName("i")] public int I { get; set; }
        [JsonPropertyName("c")] public string C { get; set; }
    }
    public class ClassificationResult
    {
        [JsonPropertyName("results")] public List<ClassificationItem> Results { get; set; }
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

    public class MergeHistory
    {
        public List<string> MergedFiles { get; set; } = new();
    }

    class Program
    {
        static readonly HttpClient httpClient = new HttpClient();
        static string configFile = "config.json";

        static async Task Main(string[] args)
        {
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            if (args.Length == 0)
                await InteractiveMode();
            else
                await CommandLineMode(args);
        }

        static async Task InteractiveMode()
        {
            var config = LoadConfig(configFile);

            while (true)
            {
                Console.WriteLine("\n请选择要执行的操作：");
                Console.WriteLine("1 - 统计标签/作者并输出 CSV（使用合并文件）");
                Console.WriteLine("2 - 获取视频详细标签并输出 JSON（最新历史文件）");
                Console.WriteLine("3 - 执行 1 + 2");
                Console.WriteLine("4 - 自动合并新历史文件（增量，不获取标签）");
                Console.WriteLine("5 - 自动合并 + 智能获取详细标签");
                Console.WriteLine("6 - 智能分类视频（需 merged_history_with_tags.json）");
                Console.Write("请输入数字 (1-6)：");
                string input = Console.ReadLine()?.Trim();

                switch (input)
                {
                    case "1": await RunStatisticsForLatest(config); return;
                    case "2": await RunFetchTagsForLatest(config); return;
                    case "3": await RunFetchTagsForLatest(config); await RunStatisticsForLatest(config); return;
                    case "4": await AutoMergeFiles(config, applyFetchTags: false); return;
                    case "5": await AutoMergeFiles(config, applyFetchTags: true); return;
                    case "6": await RunClassification(config); return;
                    default: Console.WriteLine("输入无效。"); break;
                }
            }
        }

        static async Task CommandLineMode(string[] args)
        {
            string inputJson = null;
            string outputCsv = null;
            bool fetchTags = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-i": case "--input": if (i + 1 < args.Length) inputJson = args[++i]; break;
                    case "-c": case "--config": if (i + 1 < args.Length) configFile = args[++i]; break;
                    case "-o": case "--output": if (i + 1 < args.Length) outputCsv = args[++i]; break;
                    case "-f": case "--fetch-tags": fetchTags = true; break;
                }
            }

            var config = LoadConfig(configFile);
            if (!string.IsNullOrEmpty(inputJson))
            {
                var items = LoadAndFilterItems(inputJson);
                if (fetchTags) await FetchAllTags(items, GetOutputPath(config, "input_with_tags.json"));
                await RunStatistics(items, config, outputCsv);
            }
        }

        // ==================== 模式 1/2/3 ====================
        static async Task RunStatisticsForLatest(Config config)
        {
            string mergedPath = GetOutputPath(config, "merged_history.json");
            if (!File.Exists(mergedPath))
            {
                Console.WriteLine("合并文件不存在，尝试使用最新历史文件。");
                var latest = GetLatestHistoryFile(config);
                if (latest == null) return;
                var items = LoadAndFilterItems(latest);
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
            await FetchAllTags(items, GetOutputPath(config, "input_with_tags.json"));
        }

        static string GetLatestHistoryFile(Config config)
        {
            var files = ScanHistoryFiles(config);
            if (files.Count == 0) { Console.WriteLine("没有历史文件。"); return null; }
            var latest = files.Last();
            Console.WriteLine($"最新文件: {Path.GetFileName(latest)}");
            return latest;
        }

        // ==================== 自动合并 4/5 ====================
        static async Task AutoMergeFiles(Config config, bool applyFetchTags)
        {
            var allFiles = ScanHistoryFiles(config);
            if (allFiles.Count == 0) { Console.WriteLine("无历史文件。"); return; }

            string mergeHistoryPath = GetOutputPath(config, "merge_history.json");
            var history = LoadMergeHistory(mergeHistoryPath);
            var newFiles = allFiles.Where(f => !history.MergedFiles.Contains(f)).ToList();

            string outputBase = GetOutputPath(config, "merged_history.json");
            string outputWithTags = GetOutputPath(config, "merged_history_with_tags.json");

            if (newFiles.Count == 0)
            {
                if (applyFetchTags)
                {
                    string source = File.Exists(outputWithTags) ? outputWithTags : outputBase;
                    if (!File.Exists(source)) { Console.WriteLine("无合并文件。"); return; }
                    var items = LoadVideoItems(source);
                    if (items == null || items.Count == 0) { Console.WriteLine("合并文件为空。"); return; }
                    Console.WriteLine("补充缺失标签...");
                    await FetchMissingTags(items, outputWithTags);
                }
                else Console.WriteLine("所有文件均已合并。");
                return;
            }

            Console.WriteLine($"新文件 {newFiles.Count} 个：");
            newFiles.ForEach(f => Console.WriteLine($"  {Path.GetFileName(f)}"));

            List<VideoItem> baseItems = null;
            if (File.Exists(outputWithTags))
            {
                baseItems = LoadVideoItems(outputWithTags);
                Console.WriteLine($"已加载带标签合并文件 ({baseItems?.Count ?? 0} 条)");
            }
            else if (File.Exists(outputBase))
            {
                baseItems = LoadVideoItems(outputBase);
                Console.WriteLine($"已加载合并文件 ({baseItems?.Count ?? 0} 条)");
            }

            var merged = MergeWithBase(baseItems, newFiles);
            merged = merged.OrderByDescending(it => it.ViewAt).ToList();

            SaveItemsToJson(merged, outputBase);
            Console.WriteLine($"合并完成，共 {merged.Count} 条，保存至 {outputBase}");

            newFiles.ForEach(f => history.MergedFiles.Add(f));
            SaveMergeHistory(mergeHistoryPath, history);

            if (applyFetchTags)
            {
                Console.WriteLine("补充标签（跳过已有）...");
                await FetchMissingTags(merged, outputWithTags);
            }
        }

        static List<VideoItem> MergeWithBase(List<VideoItem> baseItems, List<string> newFilePaths)
        {
            var result = baseItems ?? new List<VideoItem>();
            long maxViewAt = result.Count > 0 ? result.Max(it => it.ViewAt) : 0;
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            foreach (var path in newFilePaths)
            {
                var items = JsonSerializer.Deserialize<List<VideoItem>>(File.ReadAllText(path), options);
                if (items == null) continue;
                var newOnes = items.Where(it => it.ViewAt > maxViewAt).ToList();
                if (newOnes.Count > 0)
                {
                    result.AddRange(newOnes);
                    maxViewAt = Math.Max(maxViewAt, newOnes.Max(it => it.ViewAt));
                }
            }
            return result;
        }

        // ==================== 统计 ====================
        static async Task RunStatistics(List<VideoItem> items, Config config, string overrideCsvPath = null)
        {
            // 1. 构建反向映射：原始标签 → 目标标签
            var reverseMap = new Dictionary<string, string>();
            foreach (var kvp in config.MergeMapping)
            {
                string targetTag = kvp.Key;
                foreach (var sourceTag in kvp.Value)
                {
                    if (!reverseMap.ContainsKey(sourceTag))
                        reverseMap[sourceTag] = targetTag;
                }
            }

            var itemsWithDate = items.Select(item =>
            {
                DateTime date = DateTimeOffset.FromUnixTimeSeconds(item.ViewAt).LocalDateTime;
                return new { item.TagName, item.AuthorName, YearMonth = date.ToString("yyyy-MM") };
            }).Where(x => !config.ExcludeMonths.Contains(x.YearMonth)).ToList();

            // 2. 检查未映射标签（原始标签不在反向映射中）
            var unmapped = itemsWithDate.Select(x => x.TagName)
                .Distinct()
                .Where(t => !reverseMap.ContainsKey(t))
                .ToList();
            if (unmapped.Count > 0)
            {
                Console.WriteLine("错误：以下标签未在 merge_mapping 中定义，请更新 config.json：");
                foreach (var t in unmapped.OrderBy(t => t))
                    Console.WriteLine($"  - {t}");
                return;
            }

            // 3. 应用映射
            var mapped = itemsWithDate.Select(x => new
            {
                x.YearMonth,
                Tag = reverseMap[x.TagName],
                Author = x.AuthorName
            }).ToList();

            var months = mapped.Select(x => x.YearMonth).Distinct().OrderBy(m => m).ToList();

            // 标签矩阵
            var tagMat = mapped.GroupBy(x => new { x.Tag, x.YearMonth })
                .GroupBy(g => g.Key.Tag)
                .ToDictionary(g => g.Key, g => g.ToDictionary(x => x.Key.YearMonth, x => x.Count()));

            var sortedTags = tagMat.OrderByDescending(kv => kv.Value.Values.Sum()).Select(kv => kv.Key).ToList();
            string tagCsv = overrideCsvPath ?? GetOutputPath(config, "output.csv");
            WriteTagMatrixCsv(tagCsv, sortedTags, months, tagMat);

            // 作者矩阵
            var authorMat = mapped.GroupBy(x => new { x.Author, x.YearMonth })
                .GroupBy(g => g.Key.Author)
                .ToDictionary(g => g.Key, g => g.ToDictionary(x => x.Key.YearMonth, x => x.Count()));

            var filteredAuthors = authorMat
                .Where(kv => kv.Value.Values.Sum() >= config.AuthorMinCount)
                .OrderByDescending(kv => kv.Value.Values.Sum())
                .Select(kv => kv.Key).ToList();

            string authorCsv = GetOutputPath(config, "output_authors.csv");
            WriteAuthorMatrixCsv(authorCsv, filteredAuthors, months, authorMat);

            Console.WriteLine($"标签统计 → {tagCsv}");
            Console.WriteLine($"作者统计 → {authorCsv}");
        }

        static void WriteTagMatrixCsv(string path, List<string> tags, List<string> months,
                                      Dictionary<string, Dictionary<string, int>> matrix)
        {
            using var w = new StreamWriter(path, false, Encoding.UTF8);
            w.WriteLine("Tag," + string.Join(",", months.Select(EscapeCsvField)));
            foreach (var tag in tags)
            {
                var row = new List<string> { EscapeCsvField(tag) };
                var d = matrix.TryGetValue(tag, out var dict) ? dict : new Dictionary<string, int>();
                foreach (var m in months) row.Add(d.TryGetValue(m, out int c) ? c.ToString() : "0");
                w.WriteLine(string.Join(",", row));
            }
        }

        static void WriteAuthorMatrixCsv(string path, List<string> authors, List<string> months,
                                         Dictionary<string, Dictionary<string, int>> matrix)
        {
            using var w = new StreamWriter(path, false, Encoding.UTF8);
            w.WriteLine("Author," + string.Join(",", months.Select(EscapeCsvField)));
            foreach (var a in authors)
            {
                var row = new List<string> { EscapeCsvField(a) };
                var d = matrix.TryGetValue(a, out var dict) ? dict : new Dictionary<string, int>();
                foreach (var m in months) row.Add(d.TryGetValue(m, out int c) ? c.ToString() : "0");
                w.WriteLine(string.Join(",", row));
            }
        }

        // ==================== 标签抓取 ====================
        static async Task FetchAllTags(List<VideoItem> items, string outputPath)
            => await FetchTagsInternal(items, outputPath, onlyMissing: false);
        static async Task FetchMissingTags(List<VideoItem> items, string outputPath)
            => await FetchTagsInternal(items, outputPath, onlyMissing: true);

        static async Task FetchTagsInternal(List<VideoItem> items, string outputPath, bool onlyMissing)
        {
            var toFetch = onlyMissing
                ? items.Where(it => (it.DetailTags == null || it.DetailTags.Count == 0) && it.Business == "archive").ToList()
                : items.Where(it => it.Business == "archive").ToList();

            if (toFetch.Count == 0)
            {
                Console.WriteLine("所有 archive 视频已含标签。");
                SaveItemsToJson(items, outputPath);
                return;
            }
            Console.WriteLine($"需获取标签：{toFetch.Count} / {items.Count} 条");
            int ok = 0;
            var rng = new Random();
            for (int i = 0; i < toFetch.Count; i++)
            {
                var item = toFetch[i];
                Console.Write($"\r{i + 1}/{toFetch.Count} (BV:{item.Bvid})");
                try
                {
                    item.DetailTags = await FetchVideoTags(item.Bvid);
                    ok++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"\n获取失败 BV:{item.Bvid}: {ex.Message}");
                    item.DetailTags = null;
                }
                await Task.Delay(rng.Next(150, 400));
            }
            Console.WriteLine($"\n成功 {ok}/{toFetch.Count}");
            SaveItemsToJson(items, outputPath);
        }

        static async Task<List<BiliTag>> FetchVideoTags(string bvid)
        {
            string url = $"https://api.bilibili.com/x/tag/archive/tags?bvid={bvid}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Referer", "https://www.bilibili.com/");
            var resp = await httpClient.SendAsync(req);
            resp.EnsureSuccessStatusCode();

            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
            var root = doc.RootElement;
            if (root.GetProperty("code").GetInt32() != 0) return new List<BiliTag>();
            var data = root.GetProperty("data");
            if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() == 0) return new List<BiliTag>();
            return JsonSerializer.Deserialize<List<BiliTag>>(data.GetRawText()) ?? new List<BiliTag>();
        }

        static void SaveItemsToJson(List<VideoItem> items, string path)
        {
            var opts = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            File.WriteAllText(path, JsonSerializer.Serialize(items, opts));
            Console.WriteLine($"已输出: {path}");
        }

        // ==================== 文件扫描 ====================
        static List<string> ScanHistoryFiles(Config config)
        {
            string dir = string.IsNullOrEmpty(config.InputFolder) ? "." : config.InputFolder;
            if (!Directory.Exists(dir)) return new List<string>();
            var files = Directory.GetFiles(dir, "bilibili-history-*.json")
                .Where(f => Regex.IsMatch(Path.GetFileName(f), @"^bilibili-history-(\d{4}-\d{2}-\d{2})( \(\d+\))?\.json$"))
                .ToList();
            files.Sort((a, b) =>
            {
                var da = ExtractDateAndNumber(a);
                var db = ExtractDateAndNumber(b);
                int cmp = da.Date.CompareTo(db.Date);
                if (cmp != 0) return cmp;
                return da.Number.CompareTo(db.Number);
            });
            return files;
        }

        static (DateTime Date, int Number) ExtractDateAndNumber(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var m = Regex.Match(name, @"^bilibili-history-(\d{4}-\d{2}-\d{2})( \((\d+)\))?$");
            if (!m.Success) throw new FormatException($"文件名格式错误: {name}");
            var dt = DateTime.ParseExact(m.Groups[1].Value, "yyyy-MM-dd", null);
            int num = 0;
            if (m.Groups[3].Success) num = int.Parse(m.Groups[3].Value);
            return (dt, num);
        }

        static MergeHistory LoadMergeHistory(string path)
        {
            if (!File.Exists(path)) return new MergeHistory();
            try { return JsonSerializer.Deserialize<MergeHistory>(File.ReadAllText(path)) ?? new MergeHistory(); }
            catch { return new MergeHistory(); }
        }
        static void SaveMergeHistory(string path, MergeHistory h)
        {
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(path, JsonSerializer.Serialize(h, opts));
        }

        // ==================== 基础 IO ====================
        static List<VideoItem> LoadAndFilterItems(string path)
        {
            if (!File.Exists(path)) { Console.WriteLine($"文件不存在: {path}"); Environment.Exit(1); }
            var json = File.ReadAllText(path);
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var items = JsonSerializer.Deserialize<List<VideoItem>>(json, opts);
            items = items?.Where(it => !string.IsNullOrEmpty(it.TagName) && !string.IsNullOrEmpty(it.AuthorName)).ToList();
            if (items == null || items.Count == 0) { Console.WriteLine("无有效数据。"); Environment.Exit(1); }
            return items;
        }

        static List<VideoItem>? LoadVideoItems(string path)
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<List<VideoItem>>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }

        static Config LoadConfig(string path)
        {
            if (!File.Exists(path))
            {
                Console.WriteLine($"配置文件 {path} 未找到，使用默认配置。");
                return new Config();
            }

            string json = File.ReadAllText(path);
            var config = new Config();

            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("merge_mapping", out var mergeEl))
                config.MergeMapping = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(mergeEl.GetRawText())
                                      ?? new Dictionary<string, List<string>>();
            if (doc.RootElement.TryGetProperty("exclude_months", out var excludeEl))
                config.ExcludeMonths = JsonSerializer.Deserialize<List<string>>(excludeEl.GetRawText()) ?? new();
            if (doc.RootElement.TryGetProperty("author_min_count", out var authorMinEl))
                config.AuthorMinCount = authorMinEl.GetInt32();
            if (doc.RootElement.TryGetProperty("input_folder", out var inputEl))
                config.InputFolder = inputEl.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("output_folder", out var outputEl))
                config.OutputFolder = outputEl.GetString() ?? "";

            if (doc.RootElement.TryGetProperty("classification", out var cls))
            {
                var clsOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                config.Classification = JsonSerializer.Deserialize<ClassificationConfig>(cls.GetRawText(), clsOptions)
                                        ?? new ClassificationConfig();
            }
            return config;
        }

        static string GetOutputPath(Config config, string filename)
        {
            string dir = string.IsNullOrEmpty(config.OutputFolder) ? "." : config.OutputFolder;
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, filename);
        }

        static string EscapeCsvField(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
                return $"\"{s.Replace("\"", "\"\"")}\"";
            return s;
        }

        // ==================== 模式 6：智能分类 ====================
        static async Task RunClassification(Config config)
        {
            var clsConf = config.Classification;
            if (clsConf.Categories == null || clsConf.Categories.Count == 0)
            {
                Console.WriteLine("错误：配置文件中未定义分类类别 (classification.categories)。");
                return;
            }

            string mergedPath = GetOutputPath(config, "merged_history_with_tags.json");
            if (!File.Exists(mergedPath))
            {
                Console.WriteLine($"文件不存在: {mergedPath}，请先运行模式 5 生成带标签的合并文件。");
                return;
            }

            var items = LoadVideoItems(mergedPath);
            if (items == null || items.Count == 0) { Console.WriteLine("合并文件无数据。"); return; }

            var toClassify = items.Where(it => it.Business == "archive").ToList();
            if (toClassify.Count == 0) { Console.WriteLine("没有 archive 视频。"); return; }

            Console.WriteLine($"需分类视频：{toClassify.Count} 条，类别：{string.Join(", ", clsConf.Categories)}");

            // ==================== 预分类（带冲突检测） ====================
            var preResults = new Dictionary<string, string>();
            int preHits = 0, preConflicts = 0, preUnmatched = 0;

            if (clsConf.EnablePreClassify)
            {
                (preResults, preConflicts, preUnmatched) = PreClassify(toClassify, clsConf);
                preHits = preResults.Count;
                Console.WriteLine($"预分类命中：{preHits} 条，冲突（多规则）：{preConflicts} 条，未命中：{preUnmatched} 条");
            }
            else
            {
                Console.WriteLine("预分类已禁用，全部视频将交由 AI 分类。");
                preResults = new Dictionary<string, string>();
            }

            // 剩余需AI分类的视频：未预分类的（即未命中且未冲突的）
            var toClassifyRemaining = toClassify.Where(item => !preResults.ContainsKey(item.Bvid)).ToList();
            Console.WriteLine($"剩余需AI分类：{toClassifyRemaining.Count} 条");
            // ===========================================================

            var results = new Dictionary<string, string>(preResults);

            // 预热（保持不变）
            if (clsConf.Provider?.ToLower() == "deepseek" && clsConf.EnableWarmup)
            {
                Console.WriteLine($"执行预热请求（等待 {clsConf.WarmupWaitSeconds} 秒让缓存落盘）...");
                try
                {
                    var dummyItem = new VideoItem
                    {
                        Bvid = "warmup_dummy",
                        Title = "预热占位",
                        TagName = "dummy",
                        DetailTags = new List<BiliTag> { new BiliTag { TagName = "dummy" } }
                    };
                    var dummyBatch = new List<VideoItem> { dummyItem };
                    _ = await ClassifyBatch(dummyBatch, clsConf);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"预热请求失败（不影响后续分类）: {ex.Message}");
                }
                await Task.Delay(clsConf.WarmupWaitSeconds * 1000);
                Console.WriteLine("预热完成。");
            }

            static async Task<ClassificationResponse> ClassifyBatch(List<VideoItem> batch, ClassificationConfig conf)
            {
                var categories = new List<string>(conf.Categories);
                if (!categories.Contains("其他")) categories.Add("其他");

                var prompt = new StringBuilder();
                var categoriesStr = string.Join("、", categories);
                prompt.AppendLine("你是一个专业的视频内容分类专家，擅长根据标签和元数据将视频精准归类。");
                prompt.AppendLine($"!!!严格按标签将每个视频归入以下类别之一：{categoriesStr}。");
                prompt.AppendLine("优先按较小范围标签分类，忽略标签中的广告。");
                prompt.AppendLine("如果某个视频同时符合多个类别，请选择最具体的一个,不要过度依赖大标签，应综合判断。");
                prompt.AppendLine("仅返回纯JSON对象：{\"results\":[{\"i\":序号,\"c\":\"类别\"}]}");
                prompt.AppendLine("不要包含任何额外文字或思考过程。确保输出完整。");

                for (int i = 0; i < batch.Count; i++)
                {
                    var item = batch[i];
                    var tags = item.DetailTags?.Select(t => t.TagName).ToList() ?? new List<string>();
                    prompt.AppendLine($"{i + 1}. 大标签：{item.TagName} | 标签：{string.Join("，", tags)}");
                }

                ClassificationResponse response;
                if (conf.Provider.ToLower() == "ollama")
                    response = await ClassifyWithOllama(prompt.ToString(), conf);
                else if (conf.Provider.ToLower() == "deepseek")
                    response = await ClassifyWithDeepSeek(prompt.ToString(), conf);
                else
                    return null;

                if (response == null || response.Results == null)
                {
                    var fallback = new Dictionary<string, string>();
                    foreach (var item in batch)
                        fallback[item.Bvid] = "分类失败";
                    var usage = response?.Usage ?? new UsageInfo();
                    return new ClassificationResponse { Results = fallback, Usage = usage };
                }

                var result = new Dictionary<string, string>();
                for (int i = 0; i < batch.Count; i++)
                {
                    string key = (i + 1).ToString();
                    result[batch[i].Bvid] = response.Results.TryGetValue(key, out var cat) ? cat : "未知";
                }
                return new ClassificationResponse { Results = result, Usage = response.Usage };
            }

            static async Task<ClassificationResponse> ClassifyWithOllama(string prompt, ClassificationConfig conf)
            {
                var validCategories = new List<string>(conf.Categories);
                if (!validCategories.Contains("其他")) validCategories.Add("其他");

                var jsonSchema = new
                {
                    type = "json_schema",
                    json_schema = new
                    {
                        name = "classification",
                        strict = true,
                        schema = new
                        {
                            type = "object",
                            properties = new
                            {
                                results = new
                                {
                                    type = "array",
                                    items = new
                                    {
                                        type = "object",
                                        properties = new
                                        {
                                            i = new { type = "integer" },
                                            c = new { type = "string", @enum = validCategories.ToArray() }
                                        },
                                        required = new[] { "i", "c" },
                                        additionalProperties = false
                                    }
                                }
                            },
                            required = new[] { "results" },
                            additionalProperties = false
                        }
                    }
                };

                var requestBody = new
                {
                    model = conf.OllamaModel,
                    messages = new[] { new { role = "user", content = prompt } },
                    stream = false,
                    temperature = conf.Temperature,
                    max_tokens = conf.MaxTokens,
                    response_format = jsonSchema
                };

                var jsonBody = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });
                var httpContent = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                try
                {
                    var resp = await httpClient.PostAsync($"{conf.OllamaUrl}/v1/chat/completions", httpContent);
                    resp.EnsureSuccessStatusCode();
                    var responseJson = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(responseJson);
                    var rawContent = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();

                    Console.WriteLine($"\n[Ollama 原始响应] {responseJson}");

                    var cleanJson = ExtractJson(rawContent);
                    var indexResult = ParseClassificationByIndex(cleanJson);

                    var usage = new UsageInfo();
                    if (doc.RootElement.TryGetProperty("usage", out var usageElem))
                    {
                        if (usageElem.TryGetProperty("prompt_tokens", out var pt)) usage.PromptTokens = pt.GetInt32();
                        if (usageElem.TryGetProperty("completion_tokens", out var ct)) usage.CompletionTokens = ct.GetInt32();
                        if (usageElem.TryGetProperty("total_tokens", out var tt)) usage.TotalTokens = tt.GetInt32();
                    }
                    return new ClassificationResponse { Results = indexResult, Usage = usage };
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"\nOllama 调用失败: {ex.Message}");
                    return null;
                }
            }

            static async Task<ClassificationResponse> ClassifyWithDeepSeek(string prompt, ClassificationConfig conf)
            {
                string apiKey = conf.DeepSeekApiKey;
                if (string.IsNullOrEmpty(apiKey))
                    apiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
                if (string.IsNullOrEmpty(apiKey))
                {
                    Console.WriteLine("DeepSeek API Key 未设置");
                    return null;
                }

                var requestBody = new
                {
                    model = conf.DeepSeekModel,
                    messages = new[] { new { role = "user", content = prompt } },
                    temperature = conf.Temperature,
                    max_tokens = conf.MaxTokens,
                    response_format = new { type = "json_object" },
                    thinking = new { type = "disabled" }
                };

                var jsonBody = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });

                using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.deepseek.com/v1/chat/completions");
                req.Headers.Add("Authorization", $"Bearer {apiKey}");
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                try
                {
                    var resp = await httpClient.SendAsync(req);
                    resp.EnsureSuccessStatusCode();
                    var responseJson = await resp.Content.ReadAsStringAsync();

                    Console.WriteLine($"\n[DeepSeek 原始响应] {responseJson}");

                    using var doc = JsonDocument.Parse(responseJson);
                    var rawContent = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                    var indexResult = ParseClassificationByIndex(ExtractJson(rawContent));

                    var usage = new UsageInfo();
                    if (doc.RootElement.TryGetProperty("usage", out var usageElem))
                    {
                        if (usageElem.TryGetProperty("prompt_tokens", out var pt)) usage.PromptTokens = pt.GetInt32();
                        if (usageElem.TryGetProperty("completion_tokens", out var ct)) usage.CompletionTokens = ct.GetInt32();
                        if (usageElem.TryGetProperty("total_tokens", out var tt)) usage.TotalTokens = tt.GetInt32();
                        if (usageElem.TryGetProperty("prompt_cache_hit_tokens", out var hit)) usage.PromptCacheHitTokens = hit.GetInt32();
                        if (usageElem.TryGetProperty("prompt_cache_miss_tokens", out var miss)) usage.PromptCacheMissTokens = miss.GetInt32();
                    }
                    return new ClassificationResponse { Results = indexResult, Usage = usage };
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"DeepSeek 错误: {ex.Message}");
                    return null;
                }
            }

            static string ExtractJson(string raw)
            {
                if (string.IsNullOrWhiteSpace(raw)) return raw;

                var cleaned = Regex.Replace(raw, @"<[^>]*>", "").Trim();

                var mdMatch = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase);
                if (mdMatch.Success) cleaned = mdMatch.Groups[1].Value.Trim();

                int start = cleaned.IndexOf('{');
                if (start == -1) return cleaned;

                int braceCount = 0;
                bool inString = false, escaped = false;
                for (int i = start; i < cleaned.Length; i++)
                {
                    char c = cleaned[i];
                    if (inString)
                    {
                        if (escaped) { escaped = false; continue; }
                        if (c == '\\') { escaped = true; continue; }
                        if (c == '"') inString = false;
                    }
                    else
                    {
                        if (c == '"') inString = true;
                        else if (c == '{') braceCount++;
                        else if (c == '}')
                        {
                            braceCount--;
                            if (braceCount == 0)
                            {
                                var jsonCandidate = cleaned.Substring(start, i - start + 1);
                                jsonCandidate = jsonCandidate.TrimEnd(',', ' ', '\t', '\n', '\r', '`');
                                if (!jsonCandidate.EndsWith("}"))
                                {
                                    int lastBrace = jsonCandidate.LastIndexOf('}');
                                    if (lastBrace >= 0) jsonCandidate = jsonCandidate.Substring(0, lastBrace + 1);
                                }
                                return jsonCandidate;
                            }
                        }
                    }
                }
                return cleaned.Substring(start).TrimEnd(',', ' ', '\t', '\n', '\r', '`');
            }

            static Dictionary<string, string> ParseClassificationByIndex(string jsonContent)
            {
                if (string.IsNullOrWhiteSpace(jsonContent)) return null;

                try
                {
                    using var doc = JsonDocument.Parse(jsonContent);
                    var root = doc.RootElement;

                    if (!root.TryGetProperty("results", out var resultsArray) || resultsArray.ValueKind != JsonValueKind.Array)
                        return null;

                    var dict = new Dictionary<string, string>();
                    foreach (var item in resultsArray.EnumerateArray())
                    {
                        if (!item.TryGetProperty("i", out var iElem)) continue;
                        string idx;
                        if (iElem.ValueKind == JsonValueKind.Number)
                            idx = iElem.GetInt32().ToString();
                        else if (iElem.ValueKind == JsonValueKind.String)
                            idx = iElem.GetString();
                        else
                            continue;

                        if (!item.TryGetProperty("c", out var cElem) || cElem.ValueKind != JsonValueKind.String)
                            continue;
                        string cat = cElem.GetString();

                        if (!string.IsNullOrEmpty(idx) && !string.IsNullOrEmpty(cat))
                            dict[idx] = cat;
                    }
                    return dict.Count > 0 ? dict : null;
                }
                catch (JsonException)
                {
                    return ParseClassificationByIndexFallback(jsonContent);
                }
            }

            static Dictionary<string, string> ParseClassificationByIndexFallback(string jsonContent)
            {
                var dict = new Dictionary<string, string>();
                var matches = Regex.Matches(jsonContent,
                    @"""i""\s*:\s*(?:(\d+)|""(\d+)"")\s*,\s*""c""\s*:\s*""([^""]*)""");
                foreach (Match m in matches)
                {
                    string idx = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                    string cat = m.Groups[3].Value;
                    if (!string.IsNullOrEmpty(idx))
                        dict[idx] = cat;
                }
                return dict.Count > 0 ? dict : null;
            }

            int batchSize = Math.Max(1, clsConf.BatchSize);
            var batches = new List<List<VideoItem>>();
            for (int i = 0; i < toClassifyRemaining.Count; i += batchSize)
                batches.Add(toClassifyRemaining.GetRange(i, Math.Min(batchSize, toClassifyRemaining.Count - i)));

            int batchIdx = 0;
            var semaphore = new SemaphoreSlim(clsConf.MaxConcurrency);
            var stopwatchTotal = Stopwatch.StartNew();
            var batchTimes = new ConcurrentBag<double>();

            var tasks = batches.Select(async (batch, index) =>
            {
                await semaphore.WaitAsync();
                var sw = Stopwatch.StartNew();
                try
                {
                    var response = await ClassifyBatch(batch, clsConf);
                    sw.Stop();
                    int currentBatch = Interlocked.Increment(ref batchIdx);
                    Console.WriteLine($"批次 {currentBatch}/{batches.Count} 完成，耗时 {sw.Elapsed.TotalSeconds:F2}s");
                    if (index != batches.Count - 1)
                        batchTimes.Add(sw.Elapsed.TotalSeconds);
                    return response;
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();

            var batchResults = await Task.WhenAll(tasks);
            stopwatchTotal.Stop();

            if (batchTimes.Any())
            {
                var avgTime = batchTimes.Average();
                Console.WriteLine($"\n去除最后一个批次后，平均批次耗时: {avgTime:F2} 秒（基于 {batchTimes.Count} 个批次）");
            }
            else
            {
                Console.WriteLine("\n没有足够的批次来计算平均耗时（至少需要2个批次）。");
            }

            Console.WriteLine($"\n分类全部完成！总耗时 ({stopwatchTotal.Elapsed.TotalSeconds:F2} 秒)");

            // 合并 AI 结果
            var validResponses = new List<ClassificationResponse>();
            foreach (var response in batchResults)
            {
                if (response != null)
                {
                    validResponses.Add(response);
                    if (response.Results != null)
                    {
                        foreach (var kv in response.Results)
                            results[kv.Key] = kv.Value;
                    }
                }
            }

            // 补全未分类（包括 AI 失败）
            foreach (var item in toClassify)
            {
                if (!results.ContainsKey(item.Bvid))
                    results[item.Bvid] = "分类失败";
            }

            Console.WriteLine($"\n分类完成，共 {results.Count} 条结果。");

            // Token 统计（保持不变）
            int totalPromptTokens = 0, totalCompletionTokens = 0, totalTokens = 0;
            int totalCacheHit = 0, totalCacheMiss = 0;
            bool hasCacheData = false;
            foreach (var resp in validResponses)
            {
                var u = resp.Usage;
                if (u != null)
                {
                    totalPromptTokens += u.PromptTokens;
                    totalCompletionTokens += u.CompletionTokens;
                    totalTokens += u.TotalTokens;
                    if (u.PromptCacheHitTokens.HasValue && u.PromptCacheMissTokens.HasValue)
                    {
                        totalCacheHit += u.PromptCacheHitTokens.Value;
                        totalCacheMiss += u.PromptCacheMissTokens.Value;
                        hasCacheData = true;
                    }
                }
            }

            Console.WriteLine("\n========== API 用量统计 ==========");
            Console.WriteLine($"总 Prompt Tokens: {totalPromptTokens}");
            Console.WriteLine($"总 Completion Tokens: {totalCompletionTokens}");
            Console.WriteLine($"总 Tokens (输入+输出): {totalTokens}");
            if (hasCacheData && totalPromptTokens > 0)
            {
                double cacheHitPercent = (double)totalCacheHit / totalPromptTokens * 100;
                Console.WriteLine($"DeepSeek 缓存命中 Tokens: {totalCacheHit} (占总 Prompt Tokens 的 {cacheHitPercent:F2}%)");
                Console.WriteLine($"DeepSeek 缓存未命中 Tokens: {totalCacheMiss}");
            }
            else
            {
                Console.WriteLine("未检测到缓存数据（可能未启用缓存或非 DeepSeek 请求）");
            }
            Console.WriteLine("=====================================\n");

            // 输出 CSV（含 PreClassified 列）
            string csvPath = GetOutputPath(config, "classification.csv");
            using var writer = new StreamWriter(csvPath, false, Encoding.UTF8);
            writer.WriteLine("URL,Title,DefaultTag,DetailTags,Category,PreClassified");
            foreach (var item in toClassify)
            {
                string url = $"https://www.bilibili.com/video/{item.Bvid}";
                string title = EscapeCsvField(item.Title ?? "");
                string defTag = EscapeCsvField(item.TagName ?? "");
                string detailTags = item.DetailTags != null
                    ? EscapeCsvField(string.Join(", ", item.DetailTags.Select(t => t.TagName)))
                    : "";
                string cat = results.TryGetValue(item.Bvid, out var c) ? c : "未知";
                bool preClassified = preResults.ContainsKey(item.Bvid);
                writer.WriteLine($"{url},{title},{defTag},{detailTags},{EscapeCsvField(cat)},{preClassified}");
            }
            Console.WriteLine($"分类结果已写入: {csvPath}");
        }

        // ==================== 预分类方法（冲突检测版） ====================
        static (Dictionary<string, string> preResults, int conflicts, int unmatched) PreClassify(List<VideoItem> items, ClassificationConfig conf)
        {
            var result = new Dictionary<string, string>();
            var rules = conf.PreClassifyRules;
            if (rules == null || rules.Count == 0)
                return (result, 0, items.Count);

            int conflictCount = 0;
            int unmatchedCount = 0;

            // 用于调试输出冲突和未命中视频的详情（仅输出前几个）
            int maxDisplay = 5;
            int displayedConflict = 0, displayedUnmatched = 0;

            foreach (var item in items)
            {
                // 统计该视频匹配的所有类别
                var matchedCategories = new List<string>();

                foreach (var rule in rules)
                {
                    string category = rule.Key;
                    var ruleObj = rule.Value;
                    var fields = ruleObj.Fields;
                    if (fields == null || fields.Count == 0)
                        fields = new List<string> { "title", "tag_name", "detail_tags" };

                    bool requireAll = ruleObj.RequireAllFields;

                    var fieldMatches = new Dictionary<string, bool>();
                    foreach (var field in fields)
                    {
                        string text = "";
                        if (field == "title") text = item.Title ?? "";
                        else if (field == "tag_name") text = item.TagName ?? "";
                        else if (field == "detail_tags")
                        {
                            if (item.DetailTags != null)
                                text = string.Join(" ", item.DetailTags.Select(t => t.TagName));
                        }
                        bool fieldHit = false;
                        if (!string.IsNullOrEmpty(text))
                        {
                            fieldHit = ruleObj.Keywords.Any(k => text.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
                        }
                        fieldMatches[field] = fieldHit;
                    }

                    bool matched;
                    if (requireAll)
                        matched = fields.All(f => fieldMatches.TryGetValue(f, out bool hit) && hit);
                    else
                        matched = fields.Any(f => fieldMatches.TryGetValue(f, out bool hit) && hit);

                    if (matched)
                        matchedCategories.Add(category);
                }

                // 根据匹配数量决定是否预分类
                if (matchedCategories.Count == 0)
                {
                    unmatchedCount++;
                    if (displayedUnmatched < maxDisplay)
                    {
                        Console.WriteLine($"未匹配: Bvid={item.Bvid}, Title={item.Title}, TagName={item.TagName}, DetailTags={(item.DetailTags != null ? string.Join(",", item.DetailTags.Select(t => t.TagName)) : "null")}");
                        displayedUnmatched++;
                    }
                }
                else if (matchedCategories.Count == 1)
                {
                    result[item.Bvid] = matchedCategories[0];
                }
                else // >=2
                {
                    conflictCount++;
                    if (displayedConflict < maxDisplay)
                    {
                        Console.WriteLine($"冲突 (匹配{matchedCategories.Count}个规则): Bvid={item.Bvid}, 匹配类别={string.Join(",", matchedCategories)}");
                        displayedConflict++;
                    }
                }
            }

            if (unmatchedCount > maxDisplay)
                Console.WriteLine($"... 还有 {unmatchedCount - maxDisplay} 个未匹配视频未显示");
            if (conflictCount > maxDisplay)
                Console.WriteLine($"... 还有 {conflictCount - maxDisplay} 个冲突视频未显示");

            return (result, conflictCount, unmatchedCount);
        }
    }
}