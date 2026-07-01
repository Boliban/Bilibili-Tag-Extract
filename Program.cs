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
        public Dictionary<string, string> MergeMapping { get; set; } = new();
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
        public string OllamaModel { get; set; } = "qwen2.5:3b";   // 推荐模型

        [JsonPropertyName("deepseek_api_key")]
        public string DeepSeekApiKey { get; set; } = "";

        [JsonPropertyName("deepseek_model")]
        public string DeepSeekModel { get; set; } = "deepseek-chat";

        public List<string> Categories { get; set; } = new();

        [JsonPropertyName("batch_size")]
        public int BatchSize { get; set; } = 10;

        // 新增控制参数
        [JsonPropertyName("temperature")]
        public double Temperature { get; set; } = 0.1;          // 越低越确定，推荐 0~0.3

        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; set; } = 1024;              // 输出 token 上限

        [JsonPropertyName("max_concurrency")]
        public int MaxConcurrency { get; set; } = 3;            // 并行批次数，根据 API 限制调整
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
            var withDate = items.Select(it =>
            {
                var dt = DateTimeOffset.FromUnixTimeSeconds(it.ViewAt).LocalDateTime;
                return new { it.TagName, it.AuthorName, YearMonth = dt.ToString("yyyy-MM") };
            }).Where(x => !config.ExcludeMonths.Contains(x.YearMonth)).ToList();

            var unmapped = withDate.Select(x => x.TagName).Distinct()
                .Where(t => !config.MergeMapping.ContainsKey(t)).ToList();
            if (unmapped.Count > 0)
            {
                Console.WriteLine("未映射标签：");
                unmapped.ForEach(t => Console.WriteLine($"  - {t}"));
                return;
            }

            var mapped = withDate.Select(x => new
            {
                x.YearMonth,
                Tag = config.MergeMapping[x.TagName],
                Author = x.AuthorName
            }).ToList();

            var months = mapped.Select(x => x.YearMonth).Distinct().OrderBy(m => m).ToList();

            var tagMat = mapped.GroupBy(x => new { x.Tag, x.YearMonth })
                .GroupBy(g => g.Key.Tag)
                .ToDictionary(g => g.Key, g => g.ToDictionary(x => x.Key.YearMonth, x => x.Count()));

            var sortedTags = tagMat.OrderByDescending(kv => kv.Value.Values.Sum()).Select(kv => kv.Key).ToList();
            string tagCsv = overrideCsvPath ?? GetOutputPath(config, "output.csv");
            WriteTagMatrixCsv(tagCsv, sortedTags, months, tagMat);

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

        static List<VideoItem> LoadVideoItems(string path)
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
                config.MergeMapping = JsonSerializer.Deserialize<Dictionary<string, string>>(mergeEl.GetRawText()) ?? new();
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

            // 只分类 archive 视频
            var toClassify = items.Where(it => it.Business == "archive").ToList();
            if (toClassify.Count == 0) { Console.WriteLine("没有 archive 视频。"); return; }

            Console.WriteLine($"需分类视频：{toClassify.Count} 条，类别：{string.Join(", ", clsConf.Categories)}");
            int batchSize = Math.Max(1, clsConf.BatchSize);
            var batches = new List<List<VideoItem>>();
            for (int i = 0; i < toClassify.Count; i += batchSize)
                batches.Add(toClassify.GetRange(i, Math.Min(batchSize, toClassify.Count - i)));

            // 分类结果字典：bvid -> category
            var results = new Dictionary<string, string>();
            int batchIdx = 0;
            // 控制并发数量的信号量
            var semaphore = new SemaphoreSlim(clsConf.MaxConcurrency);
            var tasks = batches.Select(async batch =>
            {
                await semaphore.WaitAsync();
                try
                {
                    return await ClassifyBatch(batch, clsConf);
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();

            // 等待所有批次完成
            var batchResults = await Task.WhenAll(tasks);

            // 合并结果
            foreach (var (batch, batchResult) in batches.Zip(batchResults))
            {
                if (batchResult != null)
                {
                    foreach (var kv in batchResult)
                        results[kv.Key] = kv.Value;
                }
                else
                {
                    // 批次失败，标记为“分类失败”
                    foreach (var item in batch)
                        results[item.Bvid] = "分类失败";
                }
            }
            Console.WriteLine($"\n分类完成，共 {results.Count} 条结果。");

            // 输出 CSV
            string csvPath = GetOutputPath(config, "classification.csv");
            using var writer = new StreamWriter(csvPath, false, Encoding.UTF8);
            writer.WriteLine("URL,Title,DefaultTag,DetailTags,Category");
            foreach (var item in toClassify)
            {
                string url = $"https://www.bilibili.com/video/{item.Bvid}";
                string title = EscapeCsvField(item.Title ?? "");
                string defTag = EscapeCsvField(item.TagName ?? "");
                string detailTags = item.DetailTags != null
                    ? EscapeCsvField(string.Join(", ", item.DetailTags.Select(t => t.TagName)))
                    : "";
                string cat = results.TryGetValue(item.Bvid, out var c) ? c : "未知";
                writer.WriteLine($"{url},{title},{defTag},{detailTags},{EscapeCsvField(cat)}");
            }
            Console.WriteLine($"分类结果已写入: {csvPath}");
        }

        static async Task<Dictionary<string, string>> ClassifyBatch(List<VideoItem> batch, ClassificationConfig conf)
        {
            // 构建类别列表（若没有“其他”则自动添加）
            var categories = new List<string>(conf.Categories);
            if (!categories.Contains("其他")) categories.Add("其他");

            // 强化提示词
            var prompt = new StringBuilder();
            var categoriesStr = string.Join("、", categories);
            prompt.AppendLine("你是一个专业的视频内容分类专家，擅长根据标签和元数据将视频精准归类。");
            prompt.AppendLine($"严格按标签将每个视频归入以下类别之一：{categoriesStr}。");
            prompt.AppendLine("注意：c字段的值必须完全匹配上述列表中的某个字符串，不得自创或拼接，优先按较小范围标签分类。");
            prompt.AppendLine("如果某个视频同时符合多个类别，请选择最具体的一个,不要过度依赖大标签，应综合判断，忽略标签中的广告。");
            prompt.AppendLine("仅返回纯JSON对象：{\"results\":[{\"i\":序号,\"c\":\"类别\"}]}");
            prompt.AppendLine("不要包含任何额外文字或思考过程。确保输出完整。");

            for (int i = 0; i < batch.Count; i++)
            {
                var item = batch[i];
                var tags = item.DetailTags?.Select(t => t.TagName).ToList() ?? new List<string>();
                prompt.AppendLine($"{i + 1}. 大标签：{item.TagName} | 标签：{string.Join("，", tags)}");
            }

            // 根据 provider 调用 API
            Dictionary<string, string> indexResult;
            if (conf.Provider.ToLower() == "ollama")
                indexResult = await ClassifyWithOllama(prompt.ToString(), conf);
            else if (conf.Provider.ToLower() == "deepseek")
                indexResult = await ClassifyWithDeepSeek(prompt.ToString(), conf);
            else
                return null;

            // 索引 → bvid 映射
            var result = new Dictionary<string, string>();
            if (indexResult != null)
            {
                for (int i = 0; i < batch.Count; i++)
                {
                    string key = (i + 1).ToString();
                    result[batch[i].Bvid] = indexResult.TryGetValue(key, out var cat) ? cat : "未知";
                }
            }
            else
            {
                batch.ForEach(item => result[item.Bvid] = "分类失败");
            }
            return result;
        }

        static async Task<Dictionary<string, string>> ClassifyWithOllama(string prompt, ClassificationConfig conf)
        {
            var requestBody = new
            {
                model = conf.OllamaModel,
                messages = new[] { new { role = "user", content = prompt } },
                stream = false,
                temperature = conf.Temperature,
                max_tokens = conf.MaxTokens,
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



                // ★ 关键步骤：从可能混杂思考链的内容中提取 JSON 对象
                var cleanJson = ExtractJson(rawContent);
                var result = ParseClassificationByIndex(cleanJson);
                //输出调试
                if (result == null)
                {
                    Console.WriteLine($"\n[解析失败] 提取的JSON有效，但缺少 results 数组或索引/分类字段");
                    Console.WriteLine($"提取的JSON: {cleanJson?[..Math.Min(cleanJson.Length, 300)]}");
                }

                // 解析 JSON 获取索引-类别映射
                return ParseClassificationByIndex(cleanJson);

            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nOllama 调用失败: {ex.Message}");
                return null;
            }
        }

        static async Task<Dictionary<string, string>> ClassifyWithDeepSeek(string prompt, ClassificationConfig conf)
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
                response_format = new { type = "json_object" }
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

                // ★ 输出原始响应到控制台（用于调试）
                Console.WriteLine($"\n[DeepSeek 原始响应] {responseJson}");

                using var doc = JsonDocument.Parse(responseJson);
                var rawContent = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                return ParseClassificationByIndex(ExtractJson(rawContent));
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

            // 移除思考链标签（如 <think>）
            var cleaned = Regex.Replace(raw, @"<[^>]*>", "").Trim();

            // 提取 Markdown 代码块内的 JSON
            var mdMatch = Regex.Match(cleaned, @"```(?:json)?\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase);
            if (mdMatch.Success) cleaned = mdMatch.Groups[1].Value.Trim();

            // 找到第一个 '{' 的位置
            int start = cleaned.IndexOf('{');
            if (start == -1) return cleaned;

            // 从起始大括号开始，手动配对大括号
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
                            // 截取到配对的右大括号，并去除尾部多余字符
                            var jsonCandidate = cleaned.Substring(start, i - start + 1);
                            // 再次确保尾部没有非 JSON 字符（如反引号、逗号、空格）
                            jsonCandidate = jsonCandidate.TrimEnd(',', ' ', '\t', '\n', '\r', '`');
                            // 如果最后一个字符不是 '}'，重新寻找真正的结尾
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
            // 无完整配对，返回从第一个 '{' 开始的剩余部分
            return cleaned.Substring(start).TrimEnd(',', ' ', '\t', '\n', '\r', '`');
        }

        // 辅助类（放在 Program.cs 类定义区域，如 MergeHistory 后面）
        public class ClassificationItem
        {
            [JsonPropertyName("i")] public int I { get; set; }
            [JsonPropertyName("c")] public string C { get; set; }
        }
        public class ClassificationResult
        {
            [JsonPropertyName("results")] public List<ClassificationItem> Results { get; set; }
        }

        // 解析方法
        static Dictionary<string, string> ParseClassificationByIndex(string jsonContent)
        {
            if (string.IsNullOrWhiteSpace(jsonContent)) return null;

            var dict = new Dictionary<string, string>();
            // 匹配独立的分类对象：{"i":数字, "c":"类别"} 或 {"i":"数字", "c":"类别"}
            var matches = Regex.Matches(jsonContent,
                @"\{""i""\s*:\s*(?:(\d+)|""(\d+)"")\s*,\s*""c""\s*:\s*""([^""]*)""\}");
            foreach (Match m in matches)
            {
                string idx = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                string cat = m.Groups[3].Value;
                if (!string.IsNullOrEmpty(idx))
                    dict[idx] = cat;
            }
            return dict.Count > 0 ? dict : null;
        }
    }
}