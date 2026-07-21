using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace VideoTagProcessor;

public static class FileHelper
{
    public static List<VideoItem> LoadAndFilterItems(string path)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine($"文件不存在: {path}");
            Environment.Exit(1);
        }
        var json = File.ReadAllText(path);
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var items = JsonSerializer.Deserialize<List<VideoItem>>(json, opts);
        items = items?.Where(it => !string.IsNullOrEmpty(it.TagName) && !string.IsNullOrEmpty(it.AuthorName)).ToList();
        if (items == null || items.Count == 0)
        {
            Console.WriteLine("无有效数据。");
            Environment.Exit(1);
        }
        return items;
    }

    public static List<VideoItem>? LoadVideoItems(string path)
    {
        if (!File.Exists(path)) return null;
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<List<VideoItem>>(File.ReadAllText(path), opts);
    }

    public static void SaveItemsToJson(List<VideoItem> items, string path)
    {
        var opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        File.WriteAllText(path, JsonSerializer.Serialize(items, opts));
        Console.WriteLine($"已输出: {path}");
    }

    public static string GetOutputPath(Config config, string filename)
    {
        string dir = string.IsNullOrEmpty(config.OutputFolder) ? "." : config.OutputFolder;
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, filename);
    }
}