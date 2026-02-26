using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using AssetPipeline.Core.Models;

namespace AssetPipeline.Core.Data
{
    // Entity Framework Core DbContext for the pipeline's SQLite database.
    public class PipelineDbContext : DbContext
    {
        public DbSet<ProcessedAsset> ProcessedAssets => Set<ProcessedAsset>();
        public DbSet<PipelineLog> PipelineLogs => Set<PipelineLog>();

        public string DbPath { get; }

        public PipelineDbContext()
        {
            // Storing the database in a shared location so that both the projects can access
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AssetPipeline"
            );
            Directory.CreateDirectory(appData);
            DbPath = Path.Combine(appData, "pipeline.db");
        }

        public PipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options)
        {
            DbPath = string.Empty;
        }

        protected override void OnConfiguring(DbContextOptionsBuilder options)
        {
            if (!options.IsConfigured)
            {
                options.UseSqlite($"Data Source={DbPath}");
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ProcessedAsset>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.FullPath).IsUnique();
                entity.HasIndex(e => e.FileHash);
                entity.HasIndex(e => e.Category);
                entity.HasIndex(e => e.Status);
            });

            modelBuilder.Entity<PipelineLog>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.TimestampUtc);
                entity.HasIndex(e => e.EventType);
            });
        }
    }
}
