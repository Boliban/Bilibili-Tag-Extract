using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace VideoTagProcessor;

public static class FileMerger
{
    public static async Task AutoMergeFiles(Config config, bool applyFetchTags)
    {
        var allFiles = ScanHistoryFiles(config);
        if (allFiles.Count == 0) { Console.WriteLine("无历史文件。"); return; }

        string mergeHistoryPath = FileHelper.GetOutputPath(config, "merge_history.json");
        var history = LoadMergeHistory(mergeHistoryPath);
        var newFiles = allFiles.Where(f => !history.MergedFiles.Contains(f)).ToList();

        string outputBase = FileHelper.GetOutputPath(config, "merged_history.json");
        string outputWithTags = FileHelper.GetOutputPath(config, "merged_history_with_tags.json");

        if (newFiles.Count == 0)
        {
            if (applyFetchTags)
            {
                string source = File.Exists(outputWithTags) ? outputWithTags : outputBase;
                if (!File.Exists(source)) { Console.WriteLine("无合并文件。"); return; }
                var items = FileHelper.LoadVideoItems(source);
                if (items == null || items.Count == 0) { Console.WriteLine("合并文件为空。"); return; }
                Console.WriteLine("补充缺失标签...");
                await TagFetcher.FetchMissingTags(items, outputWithTags);
            }
            else
                Console.WriteLine("所有文件均已合并。");
            return;
        }

        Console.WriteLine($"新文件 {newFiles.Count} 个：");
        newFiles.ForEach(f => Console.WriteLine($"  {Path.GetFileName(f)}"));

        List<VideoItem>? baseItems = null;
        if (File.Exists(outputWithTags))
        {
            baseItems = FileHelper.LoadVideoItems(outputWithTags);
            Console.WriteLine($"已加载带标签合并文件 ({baseItems?.Count ?? 0} 条)");
        }
        else if (File.Exists(outputBase))
        {
            baseItems = FileHelper.LoadVideoItems(outputBase);
            Console.WriteLine($"已加载合并文件 ({baseItems?.Count ?? 0} 条)");
        }

        var merged = MergeWithBase(baseItems, newFiles);
        merged = merged.OrderByDescending(it => it.ViewAt).ToList();

        FileHelper.SaveItemsToJson(merged, outputBase);
        Console.WriteLine($"合并完成，共 {merged.Count} 条，保存至 {outputBase}");

        newFiles.ForEach(f => history.MergedFiles.Add(f));
        SaveMergeHistory(mergeHistoryPath, history);

        if (applyFetchTags)
        {
            Console.WriteLine("补充标签（跳过已有）...");
            await TagFetcher.FetchMissingTags(merged, outputWithTags);
        }
    }

    private static List<VideoItem> MergeWithBase(List<VideoItem>? baseItems, List<string> newFilePaths)
    {
        var result = baseItems ?? new List<VideoItem>();
        long maxViewAt = result.Count > 0 ? result.Max(it => it.ViewAt) : 0;
        var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        foreach (var path in newFilePaths)
        {
            var items = System.Text.Json.JsonSerializer.Deserialize<List<VideoItem>>(File.ReadAllText(path), options);
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

    public static List<string> ScanHistoryFiles(Config config)
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

    private static (DateTime Date, int Number) ExtractDateAndNumber(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var m = Regex.Match(name, @"^bilibili-history-(\d{4}-\d{2}-\d{2})( \((\d+)\))?$");
        if (!m.Success) throw new FormatException($"文件名格式错误: {name}");
        var dt = DateTime.ParseExact(m.Groups[1].Value, "yyyy-MM-dd", null);
        int num = 0;
        if (m.Groups[3].Success) num = int.Parse(m.Groups[3].Value);
        return (dt, num);
    }

    private static MergeHistory LoadMergeHistory(string path)
    {
        if (!File.Exists(path)) return new MergeHistory();
        try { return System.Text.Json.JsonSerializer.Deserialize<MergeHistory>(File.ReadAllText(path)) ?? new MergeHistory(); }
        catch { return new MergeHistory(); }
    }

    private static void SaveMergeHistory(string path, MergeHistory h)
    {
        var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(h, opts));
    }
}