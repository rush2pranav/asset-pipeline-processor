using AssetPipeline.Core.Data;
using AssetPipeline.Core.Models;
using Microsoft.EntityFrameworkCore;

/*  
   Asset pipeline for the REST APi

   The following provide the endpoints to query the procesed assets,view logs and get summary stats.
   Uses ASP.NET Core Minimal APIs pattern.
 
   Endpoints:
    GET /api/assets              - lists all the assets with filters
    GET /api/assets/{id}         - helps get the single assets by their ID
    GET /api/assets/summary      - pipeline summary statistics
    GET /api/assets/categories   - categorized by asset counts
    GET /api/logs                — all the recent logs
    GET /api/health              — check
*/
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<PipelineDbContext>(options =>
{
    var dbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AssetPipeline", "pipeline.db");
    options.UseSqlite($"Data Source={dbPath}");
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "Asset Pipeline API",
        Version = "v1",
        Description = "REST API for querying game assets processed by the Asset Pipeline"
    });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PipelineDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.UseCors();
app.UseSwagger();
app.UseSwaggerUI();

// api endpoints and health checks

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "healthy",
    service = "Asset Pipeline API",
    timestamp = DateTime.UtcNow
}))
.WithName("HealthCheck")
.WithTags("System");

// GET
app.MapGet("/api/assets", async (
    PipelineDbContext db,
    string? category,
    string? status,
    string? search,
    string? sortBy,
    int page = 1,
    int pageSize = 50) =>
{
    var query = db.ProcessedAssets.AsQueryable();

    // fitering
    if (!string.IsNullOrEmpty(category))
        query = query.Where(a => a.Category == category);

    if (!string.IsNullOrEmpty(status))
        query = query.Where(a => a.Status == status);

    if (!string.IsNullOrEmpty(search))
        query = query.Where(a =>
            a.FileName.Contains(search) ||
            a.RelativePath.Contains(search) ||
            a.Extension.Contains(search));

    // SORT
    query = sortBy?.ToLower() switch
    {
        "name" => query.OrderBy(a => a.FileName),
        "size" => query.OrderByDescending(a => a.FileSizeBytes),
        "date" => query.OrderByDescending(a => a.FileModifiedUtc),
        "category" => query.OrderBy(a => a.Category).ThenBy(a => a.FileName),
        "status" => query.OrderBy(a => a.Status),
        _ => query.OrderByDescending(a => a.ProcessedAtUtc)
    };

    var total = await query.CountAsync();
    var assets = await query
        .Skip((page - 1) * pageSize)
        .Take(pageSize)
        .ToListAsync();

    return Results.Ok(new
    {
        total,
        page,
        pageSize,
        totalPages = (int)Math.Ceiling(total / (double)pageSize),
        data = assets
    });
})
.WithName("GetAssets")
.WithTags("Assets");

// GET pipelines stats
app.MapGet("/api/assets/summary", async (PipelineDbContext db) =>
{
    var assets = await db.ProcessedAssets.ToListAsync();

    var summary = new PipelineSummary
    {
        TotalAssets = assets.Count,
        CompletedAssets = assets.Count(a => a.Status == "Completed"),
        FailedAssets = assets.Count(a => a.Status == "Failed"),
        PendingAssets = assets.Count(a => a.Status == "Pending"),
        TotalSizeBytes = assets.Sum(a => a.FileSizeBytes),
        AvgProcessingTimeMs = assets.Any() ? Math.Round(assets.Average(a => a.ProcessingTimeMs), 2) : 0,
        AssetsByCategory = assets.GroupBy(a => a.Category)
            .ToDictionary(g => g.Key, g => g.Count()),
        AssetsByStatus = assets.GroupBy(a => a.Status)
            .ToDictionary(g => g.Key, g => g.Count())
    };

    return Results.Ok(summary);
})
.WithName("GetSummary")
.WithTags("Assets");

// GET browsing by category
app.MapGet("/api/assets/categories", async (PipelineDbContext db) =>
{
    var categories = await db.ProcessedAssets
        .GroupBy(a => a.Category)
        .Select(g => new
        {
            category = g.Key,
            count = g.Count(),
            totalSizeBytes = g.Sum(a => a.FileSizeBytes),
            avgProcessingTimeMs = Math.Round(g.Average(a => a.ProcessingTimeMs), 2)
        })
        .OrderByDescending(c => c.count)
        .ToListAsync();

    return Results.Ok(categories);
})
.WithName("GetCategories")
.WithTags("Assets");

// GET single asset details
app.MapGet("/api/assets/{id}", async (int id, PipelineDbContext db) =>
{
    var asset = await db.ProcessedAssets.FindAsync(id);
    return asset is null ? Results.NotFound() : Results.Ok(asset);
})
.WithName("GetAssetById")
.WithTags("Assets");

// GET reccent logs
app.MapGet("/api/logs", async (PipelineDbContext db, int limit = 50) =>
{
    var logs = await db.PipelineLogs
        .OrderByDescending(l => l.TimestampUtc)
        .Take(limit)
        .ToListAsync();

    return Results.Ok(logs);
})
.WithName("GetLogs")
.WithTags("Logs");

// RUN
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine(@"
   Asset Pipeline API
   Swagger UI: http://localhost:5000/swagger
");
Console.ResetColor();

app.Run("http://localhost:5000");