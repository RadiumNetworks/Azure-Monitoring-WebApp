namespace MonitoringApp;

public sealed class AgentAnalysisContextOptions
{
    public const string SectionName = "AgentAnalysisContext";

    private static readonly HashSet<AlertGraphLayer> AllowedCorrelationLayers =
    [
        AlertGraphLayer.Domain,
        AlertGraphLayer.Role,
        AlertGraphLayer.Site,
        AlertGraphLayer.ResourceGroup
    ];

    public AlertGraphLayer[] InventoryCorrelationLayers { get; init; } =
        [AlertGraphLayer.Domain, AlertGraphLayer.Role];

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (InventoryCorrelationLayers.Length == 0)
        {
            errors.Add("InventoryCorrelationLayers must contain at least one field.");
        }

        if (InventoryCorrelationLayers.Any(layer => !AllowedCorrelationLayers.Contains(layer)))
        {
            errors.Add("InventoryCorrelationLayers may contain only Domain, Role, Site, or ResourceGroup.");
        }

        if (InventoryCorrelationLayers.Distinct().Count() != InventoryCorrelationLayers.Length)
        {
            errors.Add("InventoryCorrelationLayers must not contain duplicates.");
        }

        return errors;
    }
}