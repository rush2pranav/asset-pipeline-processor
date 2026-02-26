using AssetPipeline.Core.Data;
using AssetPipeline.Core.Services;

namespace AssetPipeline.Processor
{
    class Program
    {
        private static AssetProcessingService _processor = null!;
        private static string _watchPath = string.Empty;
        private static readonly object _lock = new();

        static async Task Main(string[] args)
        {
            Console.Title = "Asset Pipeline Processor";

            PrintBanner();

            // in order to determin eht watch path
            _watchPath = args.Length > 0 ? args[0] : GetWatchPathFromUser();

            if (!Directory.Exists(_watchPath))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Directory not found: {_watchPath}");
                Console.ResetColor();
                return;
            }

            // intiialization
            var thumbnailDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AssetPipeline", "thumbnails");

            _processor = new AssetProcessingService(thumbnailDir);

            // ensure the db exists
            using (var db = new PipelineDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  Database: {db.DbPath}");
                Console.ResetColor();
            }

            // first phase for the initial scan
            await PerformInitialScan();

            // second phase to watch the changes
            StartFileWatcher();

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\n  Watching for file changes... Press 'Q' to quit, 'R' to rescan.\n");
            Console.ResetColor();

            // main loop
            while (true)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.Q)
                    {
                        Console.WriteLine("\n  Shutting down...");
                        break;
                    }
                    if (key.Key == ConsoleKey.R)
                    {
                        await PerformInitialScan();
                    }
                }
                await Task.Delay(100);
            }
        }

        // scan the directory and process the data
        static async Task PerformInitialScan()
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\n  Scanning: {_watchPath}");
            Console.ResetColor();

            var results = await _processor.ScanAndProcessDirectoryAsync(
                _watchPath,
                progress =>
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write($"\r  {progress,-80}");
                    Console.ResetColor();
                }
            );

            // save to the db
            await _processor.SaveResultsAsync(results);

            // this prints the summary
            var summary = await _processor.GetSummaryAsync();
            Console.WriteLine();
            PrintSummary(summary);
        }

        // start the system watcher to detect any real time changes
        static void StartFileWatcher()
        {
            var watcher = new FileSystemWatcher(_watchPath)
            {
                NotifyFilter = NotifyFilters.FileName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.Size
                             | NotifyFilters.CreationTime,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };

            watcher.Created += OnFileChanged;
            watcher.Changed += OnFileChanged;
            watcher.Renamed += OnFileRenamed;
            watcher.Deleted += OnFileDeleted;
        }

        static async void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            // Small delay to let file writing complete
            await Task.Delay(500);

            lock (_lock)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  [+] {e.ChangeType}: {e.Name}");
                Console.ResetColor();
            }

            try
            {
                var asset = await _processor.ProcessFileAsync(e.FullPath, _watchPath);
                await _processor.SaveResultsAsync(new List<AssetPipeline.Core.Models.ProcessedAsset> { asset });

                lock (_lock)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGreen;
                    Console.WriteLine($"       -> {asset.Status}: {asset.Category}, " +
                        $"{asset.FileSizeDisplay}, {asset.ProcessingTimeMs:F1}ms");
                    Console.ResetColor();
                }
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"       -> Error: {ex.Message}");
                    Console.ResetColor();
                }
            }
        }

        static void OnFileRenamed(object sender, RenamedEventArgs e)
        {
            lock (_lock)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  [~] Renamed: {e.OldName} → {e.Name}");
                Console.ResetColor();
            }
        }

        static void OnFileDeleted(object sender, FileSystemEventArgs e)
        {
            lock (_lock)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  [-] Deleted: {e.Name}");
                Console.ResetColor();
            }
        }

        static void PrintSummary(AssetPipeline.Core.Models.PipelineSummary summary)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  ````````````````````````````````````````");
            Console.WriteLine("          PIPELINE SUMMARY             ");
            Console.WriteLine("  ````````````````````````````````````````");
            Console.WriteLine($" Total Assets: {summary.TotalAssets,6}  ");
            Console.WriteLine($" Completed: {summary.CompletedAssets,6} ");
            Console.WriteLine($" Failed: {summary.FailedAssets,6} ");
            Console.WriteLine($" Avg Process Time: {summary.AvgProcessingTimeMs,6:F1} ms ");
            Console.WriteLine("  ````````````````````````````````````````");

            foreach (var cat in summary.AssetsByCategory.OrderByDescending(c => c.Value))
            {
                Console.WriteLine($" {cat.Key,-18} {cat.Value,6}  ");
            }

            Console.WriteLine(" `````````````````````````````````````````");
            Console.ResetColor();
        }

        static string GetWatchPathFromUser()
        {
            Console.Write("\n  Enter directory path to watch: ");
            var path = Console.ReadLine()?.Trim().Trim('"') ?? "";
            return path;
        }

        static void PrintBanner()
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(@"
            Asset Pipeline Processor v1.0
            Game Asset Processing Pipeline        
            ");
            Console.ResetColor();
        }
    }
}