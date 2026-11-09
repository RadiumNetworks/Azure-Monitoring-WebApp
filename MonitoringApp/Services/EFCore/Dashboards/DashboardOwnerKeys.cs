namespace MonitoringApp;

internal static class DashboardOwnerKeys
{
    public static string Normalize(string ownerKey, string documentKind)
    {
        var normalized = string.IsNullOrWhiteSpace(ownerKey) ? "open-access" : ownerKey.Trim();
        if (normalized.Length > 256)
            throw new ArgumentException($"The {documentKind} owner key cannot exceed 256 characters.", nameof(ownerKey));
        return normalized;
    }
}
