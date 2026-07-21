using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace VideoTagProcessor;

public static class TagFetcher
{
    public static HttpClient HttpClient { get; set; } = new HttpClient();

    public static async Task FetchAllTags(List<VideoItem> items, string outputPath)
        => await FetchTagsInternal(items, outputPath, onlyMissing: false);

    public static async Task FetchMissingTags(List<VideoItem> items, string outputPath)
        => await FetchTagsInternal(items, outputPath, onlyMissing: true);

    private static async Task FetchTagsInternal(List<VideoItem> items, string outputPath, bool onlyMissing)
    {
        var toFetch = onlyMissing
            ? items.Where(it => (it.DetailTags == null || it.DetailTags.Count == 0) && it.Business == "archive").ToList()
            : items.Where(it => it.Business == "archive").ToList();

        if (toFetch.Count == 0)
        {
            Console.WriteLine("所有 archive 视频已含标签。");
            FileHelper.SaveItemsToJson(items, outputPath);
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
                item.DetailTags = await FetchVideoTags(item.Bvid!);
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
        FileHelper.SaveItemsToJson(items, outputPath);
    }

    private static async Task<List<BiliTag>> FetchVideoTags(string bvid)
    {
        string url = $"https://api.bilibili.com/x/tag/archive/tags?bvid={bvid}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Referer", "https://www.bilibili.com/");
        var resp = await HttpClient.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var root = doc.RootElement;
        if (root.GetProperty("code").GetInt32() != 0) return new List<BiliTag>();
        var data = root.GetProperty("data");
        if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() == 0) return new List<BiliTag>();
        return JsonSerializer.Deserialize<List<BiliTag>>(data.GetRawText()) ?? new List<BiliTag>();
    }
}