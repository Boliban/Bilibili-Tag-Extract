namespace VideoTagProcessor;

class Program
{
    static readonly HttpClient httpClient = new HttpClient();
    static string configFile = "config.json";

    static async Task Main(string[] args)
    {
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        // 共享 HttpClient
        TagFetcher.HttpClient = httpClient;
        Classifier.HttpClient = httpClient;

        if (args.Length == 0)
            await InteractiveMode();
        else
            await CommandLineMode(args);
    }

    static async Task InteractiveMode()
    {
        var config = ConfigLoader.LoadConfig(configFile);

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
                case "1": await RunStatisticsForLatest(config); break;
                case "2": await RunFetchTagsForLatest(config); break;
                case "3": await RunFetchTagsForLatest(config); await RunStatisticsForLatest(config); break;
                case "4": await FileMerger.AutoMergeFiles(config, applyFetchTags: false); break;
                case "5": await FileMerger.AutoMergeFiles(config, applyFetchTags: true); break;
                case "6": await Classifier.RunClassification(config); break;
                default: Console.WriteLine("输入无效。"); break;
            }
        }
    }

    static async Task CommandLineMode(string[] args)
    {
        string? inputJson = null;
        string? outputCsv = null;
        bool fetchTags = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-i":
                case "--input":
                    if (i + 1 < args.Length) inputJson = args[++i];
                    break;
                case "-c":
                case "--config":
                    if (i + 1 < args.Length) configFile = args[++i];
                    break;
                case "-o":
                case "--output":
                    if (i + 1 < args.Length) outputCsv = args[++i];
                    break;
                case "-f":
                case "--fetch-tags":
                    fetchTags = true;
                    break;
            }
        }

        var config = ConfigLoader.LoadConfig(configFile);
        if (!string.IsNullOrEmpty(inputJson))
        {
            var items = FileHelper.LoadAndFilterItems(inputJson);
            if (fetchTags)
                await TagFetcher.FetchAllTags(items, FileHelper.GetOutputPath(config, "input_with_tags.json"));
            await StatisticsGenerator.RunStatistics(items, config, outputCsv);
        }
        else
        {
            Console.WriteLine("请指定 -i 输入文件。");
        }
    }

    static async Task RunStatisticsForLatest(Config config)
    {
        string mergedPath = FileHelper.GetOutputPath(config, "merged_history.json");
        if (!File.Exists(mergedPath))
        {
            Console.WriteLine("合并文件不存在，尝试使用最新历史文件。");
            var latest = GetLatestHistoryFile(config);
            if (latest == null) return;
            var items = FileHelper.LoadAndFilterItems(latest);
            await StatisticsGenerator.RunStatistics(items, config);
            return;
        }
        Console.WriteLine($"使用合并文件: {mergedPath}");
        var mergedItems = FileHelper.LoadAndFilterItems(mergedPath);
        await StatisticsGenerator.RunStatistics(mergedItems, config);
    }

    static async Task RunFetchTagsForLatest(Config config)
    {
        var latestFile = GetLatestHistoryFile(config);
        if (latestFile == null) return;
        var items = FileHelper.LoadAndFilterItems(latestFile);
        await TagFetcher.FetchAllTags(items, FileHelper.GetOutputPath(config, "input_with_tags.json"));
    }

    static string? GetLatestHistoryFile(Config config)
    {
        var files = FileMerger.ScanHistoryFiles(config);
        if (files.Count == 0) { Console.WriteLine("没有历史文件。"); return null; }
        var latest = files[^1];
        Console.WriteLine($"最新文件: {Path.GetFileName(latest)}");
        return latest;
    }
}