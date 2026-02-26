using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Security.Cryptography;
using AssetPipeline.Core.Data;
using AssetPipeline.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace AssetPipeline.Core.Services
{
    /* Core services that processes the game assets through the designated pipeline
     
     Pipeline stages:
     1. Discover: detects the file and computes hash
     2. Validate: check file extension and readability
     3. Extract Metadata: for the file sizes, dates and possibly image dimensions
     4. Generate Thumbnail:  for the image assets
     5. Store: saves the results to sqlite database

    */

    public class AssetProcessingService
    {
        private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tga", ".tiff", ".dds", ".svg",
            ".wav", ".mp3", ".ogg", ".flac",
            ".fbx", ".obj", ".blend", ".gltf", ".glb",
            ".json", ".xml", ".yaml", ".yml", ".csv", ".ini", ".cfg",
            ".cs", ".lua", ".py", ".shader", ".hlsl", ".glsl",
            ".txt", ".md"
        };

        private readonly string _thumbnailDir;

        public AssetProcessingService(string thumbnailDirectory)
        {
            _thumbnailDir = thumbnailDirectory;
            Directory.CreateDirectory(_thumbnailDir);
        }

        // Processing a single file through the complete pipeline
        public async Task<ProcessedAsset> ProcessFileAsync(string filePath, string rootPath)
        {
            var stopwatch = Stopwatch.StartNew();
            var asset = new ProcessedAsset
            {
                FullPath = filePath,
                FileName = Path.GetFileNameWithoutExtension(filePath),
                Extension = Path.GetExtension(filePath).ToLower(),
                RelativePath = Path.GetRelativePath(rootPath, filePath),
                DiscoveredAtUtc = DateTime.UtcNow,
                Status = "Processing"
            };

            try
            {
                // this would be for the validation
                if (!SupportedExtensions.Contains(asset.Extension))
                {
                    asset.Status = "Skipped";
                    asset.ErrorMessage = $"Unsupported extension: {asset.Extension}";
                    return asset;
                }

                var fileInfo = new FileInfo(filePath);
                if (!fileInfo.Exists)
                {
                    asset.Status = "Failed";
                    asset.ErrorMessage = "File not found";
                    return asset;
                }

                // this would be for extracting the basic metadata
                asset.FileSizeBytes = fileInfo.Length;
                asset.FileCreatedUtc = fileInfo.CreationTimeUtc;
                asset.FileModifiedUtc = fileInfo.LastWriteTimeUtc;
                asset.Category = GetCategory(asset.Extension);
                asset.MimeType = GetMimeType(asset.Extension);

                // later stage for computing the file hashing in regards to the change detection
                asset.FileHash = await ComputeFileHashAsync(filePath);

                // any  category-specific processing
                if (asset.Category == "Image")
                {
                    await ProcessImageAsync(asset);
                }

                // final stage would be to mark this as completed
                asset.Status = "Completed";
                stopwatch.Stop();
                asset.ProcessingTimeMs = stopwatch.Elapsed.TotalMilliseconds;
                asset.ProcessedAtUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                asset.Status = "Failed";
                asset.ErrorMessage = ex.Message;
                stopwatch.Stop();
                asset.ProcessingTimeMs = stopwatch.Elapsed.TotalMilliseconds;
                asset.ProcessedAtUtc = DateTime.UtcNow;
            }

            return asset;
        }
        public async Task<List<ProcessedAsset>> ScanAndProcessDirectoryAsync(
            string rootPath, Action<string>? onProgress = null)
        {
            var results = new List<ProcessedAsset>();

            if (!Directory.Exists(rootPath))
                return results;

            var files = Directory.EnumerateFiles(rootPath, "*.*", SearchOption.AllDirectories);
            int count = 0;

            foreach (var file in files)
            {
                try
                {
                    var ext = Path.GetExtension(file);
                    if (!SupportedExtensions.Contains(ext))
                        continue;

                    count++;
                    onProgress?.Invoke($"Processing ({count}): {Path.GetFileName(file)}");

                    var asset = await ProcessFileAsync(file, rootPath);
                    results.Add(asset);
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }

            return results;
        }
        // saving the processed assets
        public async Task SaveResultsAsync(List<ProcessedAsset> assets)
        {
            using var db = new PipelineDbContext();
            await db.Database.EnsureCreatedAsync();

            foreach (var asset in assets)
            {
                var existing = await db.ProcessedAssets
                    .FirstOrDefaultAsync(a => a.FullPath == asset.FullPath);

                if (existing != null)
                {
                    // Update if file changed
                    if (existing.FileHash != asset.FileHash)
                    {
                        existing.FileHash = asset.FileHash;
                        existing.FileSizeBytes = asset.FileSizeBytes;
                        existing.FileModifiedUtc = asset.FileModifiedUtc;
                        existing.Status = asset.Status;
                        existing.ProcessedAtUtc = asset.ProcessedAtUtc;
                        existing.ProcessingTimeMs = asset.ProcessingTimeMs;
                        existing.ImageWidth = asset.ImageWidth;
                        existing.ImageHeight = asset.ImageHeight;
                        existing.ErrorMessage = asset.ErrorMessage;

                        await LogEventAsync(db, "FileUpdated", asset.FileName,
                            $"Re-processed changed file: {asset.RelativePath}");
                    }
                }
                else
                {
                    db.ProcessedAssets.Add(asset);
                    await LogEventAsync(db, "FileDiscovered", asset.FileName,
                        $"New asset processed: {asset.RelativePath} ({asset.Category}, {asset.FileSizeDisplay})");
                }
            }

            await db.SaveChangesAsync();
        }

            // to get the summary stats from the db
        public async Task<PipelineSummary> GetSummaryAsync()
        {
            using var db = new PipelineDbContext();
            await db.Database.EnsureCreatedAsync();

            var assets = await db.ProcessedAssets.ToListAsync();

            return new PipelineSummary
            {
                TotalAssets = assets.Count,
                CompletedAssets = assets.Count(a => a.Status == "Completed"),
                FailedAssets = assets.Count(a => a.Status == "Failed"),
                PendingAssets = assets.Count(a => a.Status == "Pending"),
                TotalSizeBytes = assets.Sum(a => a.FileSizeBytes),
                AvgProcessingTimeMs = assets.Any() ? assets.Average(a => a.ProcessingTimeMs) : 0,
                AssetsByCategory = assets.GroupBy(a => a.Category)
                    .ToDictionary(g => g.Key, g => g.Count()),
                AssetsByStatus = assets.GroupBy(a => a.Status)
                    .ToDictionary(g => g.Key, g => g.Count())
            };
        }

        //private helpers
        private async Task<string> ComputeFileHashAsync(string filePath)
        {
            using var md5 = MD5.Create();
            using var stream = File.OpenRead(filePath);
            var hash = await md5.ComputeHashAsync(stream);
            return Convert.ToHexString(hash);
        }

        private async Task ProcessImageAsync(ProcessedAsset asset)
        {
            try
            {
                // reads image dimensions using simple binary header parsing
                var dimensions = await GetImageDimensionsAsync(asset.FullPath, asset.Extension);
                asset.ImageWidth = dimensions.Width;
                asset.ImageHeight = dimensions.Height;

                // Thumbnail path and the actual generation would use imagesharp in prod
                asset.ThumbnailPath = Path.Combine(_thumbnailDir,
                    $"{asset.FileHash}_thumb{asset.Extension}");
            }
            catch
            {
                // Image processing is non-critical
            }
        }

        private async Task<(int Width, int Height)> GetImageDimensionsAsync(string path, string ext)
        {
            var bytes = await File.ReadAllBytesAsync(path);

            // PNG with the width and height at bytes 16-23
            if (ext is ".png" && bytes.Length > 24 &&
                bytes[0] == 0x89 && bytes[1] == 0x50)
            {
                int width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
                int height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
                return (width, height);
            }

            // BMP with width at 18, height at 22
            if (ext is ".bmp" && bytes.Length > 26 &&
                bytes[0] == 0x42 && bytes[1] == 0x4D)
            {
                int width = bytes[18] | (bytes[19] << 8) | (bytes[20] << 16) | (bytes[21] << 24);
                int height = bytes[22] | (bytes[23] << 8) | (bytes[24] << 16) | (bytes[25] << 24);
                return (width, Math.Abs(height));
            }

            return (0, 0);
        }

        private static string GetCategory(string extension) => extension.ToLower() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tga" or ".tiff" or ".dds" or ".svg" => "Image",
            ".wav" or ".mp3" or ".ogg" or ".flac" => "Audio",
            ".fbx" or ".obj" or ".blend" or ".gltf" or ".glb" => "Model",
            ".json" or ".xml" or ".yaml" or ".yml" or ".csv" or ".ini" or ".cfg" => "Config",
            ".cs" or ".lua" or ".py" or ".shader" or ".hlsl" or ".glsl" => "Script",
            _ => "Other"
        };

        private static string GetMimeType(string extension) => extension.ToLower() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".bmp" => "image/bmp",
            ".gif" => "image/gif",
            ".wav" => "audio/wav",
            ".mp3" => "audio/mpeg",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".csv" => "text/csv",
            _ => "application/octet-stream"
        };

        private async Task LogEventAsync(PipelineDbContext db, string eventType,
            string fileName, string message)
        {
            db.PipelineLogs.Add(new PipelineLog
            {
                EventType = eventType,
                FileName = fileName,
                Message = message,
                TimestampUtc = DateTime.UtcNow
            });
        }
    }
}
