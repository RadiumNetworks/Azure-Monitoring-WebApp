using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitoringApp;

public sealed class AnalysisDashboardEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public AnalysisDashboardDocument Document { get; set; } = new();
}

public sealed class AnalysisDashboardCollection
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public int Version { get; set; } = 2;
    public Guid ActiveDashboardId { get; set; }
    public List<AnalysisDashboardEntry> Dashboards { get; set; } = [];
    public List<Guid> HiddenQueryIds { get; set; } = [];

    [JsonIgnore]
    public AnalysisDashboardEntry Active => Dashboards.Single(entry => entry.Id == ActiveDashboardId);

    public static AnalysisDashboardCollection Create(AnalysisDashboardDocument? existing = null)
    {
        var entry = new AnalysisDashboardEntry { Document = existing ?? new() };
        return new()
        {
            ActiveDashboardId = entry.Id,
            Dashboards = [entry],
            HiddenQueryIds = entry.Document.HiddenQueryIds.ToList()
        };
    }

    public static AnalysisDashboardCollection Deserialize(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        AnalysisDashboardCollection result;
        if (parsed.RootElement.TryGetProperty("dashboards", out _))
        {
            result = JsonSerializer.Deserialize<AnalysisDashboardCollection>(json, JsonOptions)
                ?? throw new InvalidOperationException("The saved dashboard collection is invalid.");
        }
        else
        {
            var document = JsonSerializer.Deserialize<AnalysisDashboardDocument>(json, JsonOptions)
                ?? throw new InvalidOperationException("The saved dashboard document is invalid.");
            result = Create(document);
        }
        result.Validate();
        return result;
    }

    public string Serialize()
    {
        Validate();
        return JsonSerializer.Serialize(this, JsonOptions);
    }

    public AnalysisDashboardCollection Copy() => Deserialize(Serialize());

    public AnalysisDashboardDocument OpenActive()
    {
        var document = Clone(Active.Document);
        document.HiddenQueryIds = HiddenQueryIds.ToList();
        return document;
    }

    public void CaptureActive(AnalysisDashboardDocument document)
    {
        Active.Document = Clone(document);
        HiddenQueryIds = document.HiddenQueryIds.Distinct().ToList();
    }

    public void Select(Guid id)
    {
        if (!Dashboards.Any(entry => entry.Id == id))
            throw new InvalidOperationException("The dashboard no longer exists.");
        ActiveDashboardId = id;
    }

    public void Add(string name)
    {
        var entry = new AnalysisDashboardEntry { Document = new() { Name = ValidateName(name) } };
        Dashboards.Add(entry);
        ActiveDashboardId = entry.Id;
    }

    public void RenameActive(string name) => Active.Document.Name = ValidateName(name);

    public void AddCopy(AnalysisDashboardDocument source)
    {
        var sourceName = ValidateName(source.Name);
        var document = Clone(source);
        var number = 1;
        string name;
        do
        {
            var suffix = number == 1 ? " (copy)" : $" (copy {number})";
            var length = Math.Min(sourceName.Length, 256 - suffix.Length);
            if (length > 0 && char.IsHighSurrogate(sourceName[length - 1]))
                length--;
            name = sourceName[..length] + suffix;
            number++;
        } while (Dashboards.Any(entry => entry.Document.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
        document.Name = name;
        document.SourceTemplateId = null;
        document.SourceTemplateVersion = null;
        document.HiddenQueryIds = HiddenQueryIds.ToList();
        foreach (var widget in document.Widgets)
            widget.Id = Guid.NewGuid().ToString("N");
        var entry = new AnalysisDashboardEntry { Document = document };
        Dashboards.Add(entry);
        ActiveDashboardId = entry.Id;
    }

    public void DeleteActive()
    {
        if (Dashboards.Count <= 1)
            throw new InvalidOperationException("Keep at least one dashboard.");
        var index = Dashboards.FindIndex(entry => entry.Id == ActiveDashboardId);
        Dashboards.RemoveAt(index);
        ActiveDashboardId = Dashboards[Math.Min(index, Dashboards.Count - 1)].Id;
    }

    private void Validate()
    {
        if (Version != 2 || Dashboards is null || Dashboards.Count == 0 ||
            Dashboards.Any(entry => entry.Id == Guid.Empty || entry.Document is null) ||
            Dashboards.DistinctBy(entry => entry.Id).Count() != Dashboards.Count ||
            !Dashboards.Any(entry => entry.Id == ActiveDashboardId) || HiddenQueryIds is null)
            throw new InvalidOperationException("The saved dashboard collection is invalid.");
    }

    private static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 256)
            throw new InvalidOperationException("Dashboard name is required and may contain at most 256 characters.");
        return name.Trim();
    }

    private static AnalysisDashboardDocument Clone(AnalysisDashboardDocument document) =>
        JsonSerializer.Deserialize<AnalysisDashboardDocument>(JsonSerializer.Serialize(document, JsonOptions), JsonOptions)
        ?? throw new InvalidOperationException("The dashboard document is invalid.");
}
