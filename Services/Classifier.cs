using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoTagProcessor;

public static class Classifier
{
    public static HttpClient HttpClient { get; set; } = new HttpClient();

    public static async Task RunClassification(Config config)
    {
        var clsConf = config.Classification;
        if (clsConf.Categories == null || clsConf.Categories.Count == 0)
        {
            Console.WriteLine("错误：配置文件中未定义分类类别 (classification.categories)。");
            return;
        }

        string mergedPath = FileHelper.GetOutputPath(config, "merged_history_with_tags.json");
        if (!File.Exists(mergedPath))
        {
            Console.WriteLine($"文件不存在: {mergedPath}，请先运行模式 5 生成带标签的合并文件。");
            return;
        }

        var items = FileHelper.LoadVideoItems(mergedPath);
        if (items == null || items.Count == 0) { Console.WriteLine("合并文件无数据。"); return; }

        var toClassify = items.Where(it => it.Business == "archive").ToList();
        if (toClassify.Count == 0) { Console.WriteLine("没有 archive 视频。"); return; }

        Console.WriteLine($"需分类视频：{toClassify.Count} 条，类别：{string.Join(", ", clsConf.Categories)}");

        // 预分类
        var preResults = new Dictionary<string, string>();
        int preConflicts = 0, preUnmatched = 0;
        if (clsConf.EnablePreClassify)
        {
            (preResults, preConflicts, preUnmatched) = PreClassify(toClassify, clsConf);
            Console.WriteLine($"预分类命中：{preResults.Count} 条，冲突（多规则）：{preConflicts} 条，未命中：{preUnmatched} 条");
        }
        else
        {
            Console.WriteLine("预分类已禁用，全部视频将交由 AI 分类。");
        }

        var toClassifyRemaining = toClassify.Where(item => !preResults.ContainsKey(item.Bvid!)).ToList();
        Console.WriteLine($"剩余需AI分类：{toClassifyRemaining.Count} 条");

        var results = new Dictionary<string, string>(preResults);

        // 预热
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
            if (!results.ContainsKey(item.Bvid!))
                results[item.Bvid!] = "分类失败";
        }

        Console.WriteLine($"\n分类完成，共 {results.Count} 条结果。");

        // Token 统计
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

        // 输出 CSV
        string csvPath = FileHelper.GetOutputPath(config, "classification.csv");
        using var writer = new StreamWriter(csvPath, false, Encoding.UTF8);
        writer.WriteLine("URL,Title,DefaultTag,DetailTags,Category,PreClassified");
        foreach (var item in toClassify)
        {
            string url = $"https://www.bilibili.com/video/{item.Bvid}";
            string title = CsvHelper.EscapeCsvField(item.Title ?? "");
            string defTag = CsvHelper.EscapeCsvField(item.TagName ?? "");
            string detailTags = item.DetailTags != null
                ? CsvHelper.EscapeCsvField(string.Join(", ", item.DetailTags.Select(t => t.TagName)))
                : "";
            string cat = results.TryGetValue(item.Bvid!, out var c) ? c : "未知";
            bool preClassified = preResults.ContainsKey(item.Bvid!);
            writer.WriteLine($"{url},{title},{defTag},{detailTags},{CsvHelper.EscapeCsvField(cat)},{preClassified}");
        }
        Console.WriteLine($"分类结果已写入: {csvPath}");
    }

    // ===== 预分类 =====
    private static (Dictionary<string, string> preResults, int conflicts, int unmatched) PreClassify(
        List<VideoItem> items, ClassificationConfig conf)
    {
        var result = new Dictionary<string, string>();
        var rules = conf.PreClassifyRules;
        if (rules == null || rules.Count == 0)
            return (result, 0, items.Count);

        int conflictCount = 0;
        int unmatchedCount = 0;
        int maxDisplay = 5, displayedConflict = 0, displayedUnmatched = 0;

        foreach (var item in items)
        {
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
                result[item.Bvid!] = matchedCategories[0];
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

    // ===== AI 分类 =====
    private static async Task<ClassificationResponse> ClassifyBatch(List<VideoItem> batch, ClassificationConfig conf)
    {
        var categories = new List<string>(conf.Categories);
        if (!categories.Contains("其他")) categories.Add("其他");

        var prompt = new StringBuilder();

        // ===== 插入额外提示词（如果非空） =====
        if (!string.IsNullOrWhiteSpace(conf.ExtraPrompt))
        {
            prompt.AppendLine(conf.ExtraPrompt.Trim());
            prompt.AppendLine(); // 增加空行分隔
        }

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

        ClassificationResponse? response;
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
                fallback[item.Bvid!] = "分类失败";
            var usage = response?.Usage ?? new UsageInfo();
            return new ClassificationResponse { Results = fallback, Usage = usage };
        }

        var result = new Dictionary<string, string>();
        for (int i = 0; i < batch.Count; i++)
        {
            string key = (i + 1).ToString();
            string cat = response.Results.TryGetValue(key, out var c) ? c : "未知";
            if (!categories.Contains(cat))
                cat = "其他";
            result[batch[i].Bvid!] = cat;
        }
        return new ClassificationResponse { Results = result, Usage = response.Usage };
    }

    private static async Task<ClassificationResponse> ClassifyWithOllama(string prompt, ClassificationConfig conf)
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
            response_format = jsonSchema
        };

        var jsonBody = JsonSerializer.Serialize(requestBody, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });
        var httpContent = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        try
        {
            var resp = await HttpClient.PostAsync($"{conf.OllamaUrl}/v1/chat/completions", httpContent);
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

    private static async Task<ClassificationResponse> ClassifyWithDeepSeek(string prompt, ClassificationConfig conf)
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
            var resp = await HttpClient.SendAsync(req);
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

    private static string ExtractJson(string raw)
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

    private static Dictionary<string, string>? ParseClassificationByIndex(string jsonContent)
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
                    idx = iElem.GetString()!;
                else
                    continue;

                if (!item.TryGetProperty("c", out var cElem) || cElem.ValueKind != JsonValueKind.String)
                    continue;
                string cat = cElem.GetString()!;

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

    private static Dictionary<string, string>? ParseClassificationByIndexFallback(string jsonContent)
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
}