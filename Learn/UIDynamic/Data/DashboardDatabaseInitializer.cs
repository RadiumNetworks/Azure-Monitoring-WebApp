using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using UIDynamic.Models;

namespace UIDynamic.Data;

public static class DashboardDatabaseInitializer
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static async Task InitializeAsync(
        LayoutDbContext db,
        string contentRootPath,
        CancellationToken cancellationToken = default)
    {
        var schemaPath = Path.Combine(contentRootPath, "Database", "create-database.sql");
        var demoDataPath = Path.Combine(contentRootPath, "DemoData", "dashboard-content.json");
        var demoLayoutsPath = Path.Combine(contentRootPath, "DemoData", "dashboard-layouts.json");

        await ExecuteSchemaAsync(db, schemaPath, cancellationToken);
        var demoData = await ReadDemoDataAsync(demoDataPath, cancellationToken);
        var demoLayouts = await ReadDemoLayoutsAsync(demoLayoutsPath, cancellationToken);
        await SeedMissingTablesAsync(db, demoData, cancellationToken);
        await SeedDemoLayoutsAsync(db, demoLayouts, cancellationToken);
    }

    private static async Task ExecuteSchemaAsync(
        LayoutDbContext db,
        string schemaPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(schemaPath))
        {
            throw new FileNotFoundException("The database schema file was not found.", schemaPath);
        }

        var sql = await File.ReadAllTextAsync(schemaPath, cancellationToken);
        var statements = sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var statement in statements)
        {
            await db.Database.ExecuteSqlRawAsync(statement, cancellationToken);
        }
    }

    private static async Task<DashboardDemoData> ReadDemoDataAsync(
        string demoDataPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(demoDataPath))
        {
            throw new FileNotFoundException("The dashboard demo-data file was not found.", demoDataPath);
        }

        await using var stream = File.OpenRead(demoDataPath);
        return await JsonSerializer.DeserializeAsync<DashboardDemoData>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The dashboard demo-data JSON is empty or invalid.");
    }

    private static async Task<DashboardDemoLayouts> ReadDemoLayoutsAsync(
        string demoLayoutsPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(demoLayoutsPath))
        {
            throw new FileNotFoundException("The dashboard demo-layout file was not found.", demoLayoutsPath);
        }

        await using var stream = File.OpenRead(demoLayoutsPath);
        return await JsonSerializer.DeserializeAsync<DashboardDemoLayouts>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The dashboard demo-layout JSON is empty or invalid.");
    }

    private static async Task SeedMissingTablesAsync(
        LayoutDbContext db,
        DashboardDemoData data,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        if (!await db.MetricReadings.AnyAsync(cancellationToken))
        {
            db.MetricReadings.AddRange(data.Metrics.Select(item => new MetricReading
            {
                DataSourceKey = item.DataSourceKey,
                Label = item.Label,
                Value = item.Value,
                Unit = item.Unit,
                Change = item.Change,
                RecordedAt = now.AddMinutes(-item.AgeMinutes)
            }));
        }

        if (!await db.TrendSamples.AnyAsync(cancellationToken))
        {
            db.TrendSamples.AddRange(data.TrendSamples.Select(item => new TrendSample
            {
                DataSourceKey = item.DataSourceKey,
                SeriesLabel = item.SeriesLabel,
                Value = item.Value,
                RecordedAt = now.AddMinutes(-item.AgeMinutes)
            }));
        }

        if (!await db.OperationalAlerts.AnyAsync(cancellationToken))
        {
            db.OperationalAlerts.AddRange(data.Alerts.Select(item => new OperationalAlert
            {
                DataSourceKey = item.DataSourceKey,
                Title = item.Title,
                Location = item.Location,
                Priority = item.Priority,
                RaisedAt = now.AddMinutes(-item.AgeMinutes)
            }));
        }

        if (!await db.TeamNotes.AnyAsync(cancellationToken))
        {
            db.TeamNotes.AddRange(data.TeamNotes.Select(item => new TeamNote
            {
                DataSourceKey = item.DataSourceKey,
                Content = item.Content,
                Author = item.Author,
                UpdatedAt = now.AddMinutes(-item.AgeMinutes)
            }));
        }

        if (!await db.ServiceHealthEntries.AnyAsync(cancellationToken))
        {
            db.ServiceHealthEntries.AddRange(data.ServiceHealth.Select(item => new ServiceHealthEntry
            {
                DataSourceKey = item.DataSourceKey,
                ComponentName = item.ComponentName,
                Status = item.Status,
                DisplayOrder = item.DisplayOrder,
                CheckedAt = now.AddMinutes(-item.AgeMinutes)
            }));
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task SeedDemoLayoutsAsync(
        LayoutDbContext db,
        DashboardDemoLayouts data,
        CancellationToken cancellationToken)
    {
        var ownerKeys = await db.SavedLayouts.Select(item => item.OwnerKey).ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;

        foreach (var item in data.Layouts.Where(item => !ownerKeys.Contains(item.OwnerKey)))
        {
            if (!Guid.TryParse(item.OwnerKey, out _))
            {
                throw new InvalidDataException($"Demo layout owner key '{item.OwnerKey}' is not a GUID.");
            }

            db.SavedLayouts.Add(new SavedLayout
            {
                OwnerKey = item.OwnerKey,
                Name = item.Name,
                DocumentJson = JsonSerializer.Serialize(item.Document, JsonOptions),
                DocumentVersion = item.Document.Version,
                Revision = 1,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed class DashboardDemoData
    {
        public List<MetricSeed> Metrics { get; init; } = [];
        public List<TrendSeed> TrendSamples { get; init; } = [];
        public List<AlertSeed> Alerts { get; init; } = [];
        public List<TeamNoteSeed> TeamNotes { get; init; } = [];
        public List<ServiceHealthSeed> ServiceHealth { get; init; } = [];
    }

    private sealed class DashboardDemoLayouts
    {
        public List<DashboardLayoutSeed> Layouts { get; init; } = [];
    }

    private sealed record DashboardLayoutSeed(
        string OwnerKey,
        string Name,
        DashboardDocument Document);

    private sealed record MetricSeed(
        string DataSourceKey,
        string Label,
        double Value,
        string Unit,
        double Change,
        int AgeMinutes);

    private sealed record TrendSeed(
        string DataSourceKey,
        string SeriesLabel,
        double Value,
        int AgeMinutes);

    private sealed record AlertSeed(
        string DataSourceKey,
        string Title,
        string Location,
        string Priority,
        int AgeMinutes);

    private sealed record TeamNoteSeed(
        string DataSourceKey,
        string Content,
        string Author,
        int AgeMinutes);

    private sealed record ServiceHealthSeed(
        string DataSourceKey,
        string ComponentName,
        string Status,
        int DisplayOrder,
        int AgeMinutes);
}
