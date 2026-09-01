using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using APILearning.Data;

namespace APILearning.Api;

public sealed class PayloadStore(IDbContextFactory<PayloadDbContext> contextFactory)
{
    public const int MaximumPayloadBytes = 256 * 1024;

    public async Task<PayloadRecord> AddAsync(
        JsonElement payload,
        CancellationToken cancellationToken = default)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("The payload must be a JSON object.");
        }

        var rawJson = JsonSerializer.Serialize(payload, JsonOptions.Compact);
        if (System.Text.Encoding.UTF8.GetByteCount(rawJson) > MaximumPayloadBytes)
        {
            throw new InvalidOperationException(
                $"JSON payloads may not exceed {MaximumPayloadBytes / 1024} KB.");
        }

        var now = DateTimeOffset.UtcNow;
        var record = new PayloadRecord
        {
            Id = Guid.NewGuid(),
            ReceivedAt = now,
            OccurredAt = ReadTimestamp(payload, "occurredAt") ?? ReadTimestamp(payload, "timestamp") ?? now,
            Name = ReadString(payload, "name", "title", "eventName") ?? "Unnamed event",
            Category = ReadString(payload, "category", "type") ?? "General",
            Source = ReadString(payload, "source", "system", "service") ?? "Unknown",
            Severity = Math.Clamp(ReadInteger(payload, "severity", "priority") ?? 0, 0, 10),
            Summary = ReadString(payload, "summary", "message", "description") ?? "No summary supplied",
            RawJson = JsonSerializer.Serialize(payload, JsonOptions.Indented)
        };

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Payloads.Add(record);
        await db.SaveChangesAsync(cancellationToken);
        return record;
    }

    public async Task<PayloadRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Payloads.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
    }

    public async Task<List<PayloadRecord>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var records = await db.Payloads.AsNoTracking().ToListAsync(cancellationToken);
        return records.OrderByDescending(item => item.ReceivedAt).ToList();
    }

    public async Task<List<PayloadRecord>> SearchAsync(
        PayloadFilter filter,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Payloads.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Name))
            query = query.Where(item => item.Name.Contains(filter.Name));
        if (!string.IsNullOrWhiteSpace(filter.Category))
            query = query.Where(item => item.Category == filter.Category);
        if (!string.IsNullOrWhiteSpace(filter.Source))
            query = query.Where(item => item.Source == filter.Source);
        if (filter.MinimumSeverity is not null)
            query = query.Where(item => item.Severity >= filter.MinimumSeverity);
        if (!string.IsNullOrWhiteSpace(filter.Contains))
            query = query.Where(item => item.RawJson.Contains(filter.Contains));

        var records = await query.ToListAsync(cancellationToken);
        if (filter.ReceivedAfter is not null)
            records = records.Where(item => item.ReceivedAt >= filter.ReceivedAfter).ToList();
        return records.OrderByDescending(item => item.ReceivedAt).Take(filter.Limit).ToList();
    }

    private static string? ReadString(JsonElement payload, params string[] names)
    {
        foreach (var property in payload.EnumerateObject())
        {
            if (names.Any(name => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString()?.Trim();
            }
        }
        return null;
    }

    private static int? ReadInteger(JsonElement payload, params string[] names)
    {
        foreach (var property in payload.EnumerateObject())
        {
            if (names.Any(name => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) &&
                property.Value.TryGetInt32(out var value))
            {
                return value;
            }
        }
        return null;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement payload, string name)
    {
        foreach (var property in payload.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String &&
                property.Value.TryGetDateTimeOffset(out var value))
            {
                return value;
            }
        }
        return null;
    }
}

public sealed record PayloadFilter(
    string? Name,
    string? Category,
    string? Source,
    int? MinimumSeverity,
    DateTimeOffset? ReceivedAfter,
    string? Contains,
    int Limit);

public sealed record IngestResponse(Guid Id, DateTimeOffset ReceivedAt, string Name);
public sealed record ApiError(string Error);
public sealed record QueryResponse(int Count, PayloadFilter Filter, IReadOnlyList<PayloadRecord> Items);

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Compact = new(JsonSerializerDefaults.Web);
    public static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}