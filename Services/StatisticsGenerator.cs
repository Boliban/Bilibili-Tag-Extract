using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VideoTagProcessor;

public static class StatisticsGenerator
{
    public static async Task RunStatistics(List<VideoItem> items, Config config, string? overrideCsvPath = null)
    {
        // 1. 反向映射
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

        // 2. 检查未映射
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

        // 3. 映射
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
        string tagCsv = overrideCsvPath ?? FileHelper.GetOutputPath(config, "output.csv");
        WriteTagMatrixCsv(tagCsv, sortedTags, months, tagMat);

        // 作者矩阵
        var authorMat = mapped.GroupBy(x => new { x.Author, x.YearMonth })
            .GroupBy(g => g.Key.Author)
            .ToDictionary(g => g.Key, g => g.ToDictionary(x => x.Key.YearMonth, x => x.Count()));

        var filteredAuthors = authorMat
            .Where(kv => kv.Value.Values.Sum() >= config.AuthorMinCount)
            .OrderByDescending(kv => kv.Value.Values.Sum())
            .Select(kv => kv.Key).ToList();

        string authorCsv = FileHelper.GetOutputPath(config, "output_authors.csv");
        WriteAuthorMatrixCsv(authorCsv, filteredAuthors, months, authorMat);

        Console.WriteLine($"标签统计 → {tagCsv}");
        Console.WriteLine($"作者统计 → {authorCsv}");
        await Task.CompletedTask;
    }

    private static void WriteTagMatrixCsv(string path, List<string> tags, List<string> months,
                                          Dictionary<string, Dictionary<string, int>> matrix)
    {
        using var w = new StreamWriter(path, false, Encoding.UTF8);
        w.WriteLine("Tag," + string.Join(",", months.Select(CsvHelper.EscapeCsvField)));
        foreach (var tag in tags)
        {
            var row = new List<string> { CsvHelper.EscapeCsvField(tag) };
            var d = matrix.TryGetValue(tag, out var dict) ? dict : new Dictionary<string, int>();
            foreach (var m in months)
                row.Add(d.TryGetValue(m, out int c) ? c.ToString() : "0");
            w.WriteLine(string.Join(",", row));
        }
    }

    private static void WriteAuthorMatrixCsv(string path, List<string> authors, List<string> months,
                                             Dictionary<string, Dictionary<string, int>> matrix)
    {
        using var w = new StreamWriter(path, false, Encoding.UTF8);
        w.WriteLine("Author," + string.Join(",", months.Select(CsvHelper.EscapeCsvField)));
        foreach (var a in authors)
        {
            var row = new List<string> { CsvHelper.EscapeCsvField(a) };
            var d = matrix.TryGetValue(a, out var dict) ? dict : new Dictionary<string, int>();
            foreach (var m in months)
                row.Add(d.TryGetValue(m, out int c) ? c.ToString() : "0");
            w.WriteLine(string.Join(",", row));
        }
    }
}