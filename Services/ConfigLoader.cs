using System;
using System.IO;
using System.Text.Json;

namespace VideoTagProcessor;

public static class ConfigLoader
{
    public static Config LoadConfig(string path)
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
}