using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AssetPipeline.Core.Models
{
    public class ProcessedAsset
    {
        public int Id { get; set; }
        public string FileName { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public string Extension { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;       // this is for the images, audio, models, configs and the scripts
        public long FileSizeBytes { get; set; }
        public string FileHash { get; set; } = string.Empty;        // hashingg for change detection
        public string Status { get; set; } = "Pending";             
        public string? ErrorMessage { get; set; }

        public int? ImageWidth { get; set; }
        public int? ImageHeight { get; set; }
        public string? MimeType { get; set; }
        public string? ThumbnailPath { get; set; }

        public DateTime FileCreatedUtc { get; set; }
        public DateTime FileModifiedUtc { get; set; }
        public DateTime ProcessedAtUtc { get; set; }
        public DateTime DiscoveredAtUtc { get; set; }
        public double ProcessingTimeMs { get; set; }

        public string FileSizeDisplay => FileSizeBytes switch
        {
            < 1024 => $"{FileSizeBytes} B",
            < 1024 * 1024 => $"{FileSizeBytes / 1024.0:F1} KB",
            < 1024L * 1024 * 1024 => $"{FileSizeBytes / (1024.0 * 1024):F1} MB",
            _ => $"{FileSizeBytes / (1024.0 * 1024 * 1024):F2} GB"
        };
    }

    public class PipelineLog
    {
        public int Id { get; set; }
        public string EventType { get; set; } = string.Empty; 
        public string FileName { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public DateTime TimestampUtc { get; set; }
    }
    public class PipelineSummary
    {
        public int TotalAssets { get; set; }
        public int CompletedAssets { get; set; }
        public int FailedAssets { get; set; }
        public int PendingAssets { get; set; }
        public long TotalSizeBytes { get; set; }
        public double AvgProcessingTimeMs { get; set; }
        public Dictionary<string, int> AssetsByCategory { get; set; } = new();
        public Dictionary<string, int> AssetsByStatus { get; set; } = new();
    }
}
