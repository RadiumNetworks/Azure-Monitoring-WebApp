namespace MonitoringApp;

/// <summary>
/// Creates user-authored logbook entries from comments saved on inbox alerts.
/// </summary>
public static class AlertCommentLogbook
{
    public static LogbookEntry? CreateEntry(
        AlertRecord alert,
        string user,
        string comment,
        DateTimeOffset createdAt)
    {
        var normalizedComment = comment.Trim();
        if (string.IsNullOrWhiteSpace(normalizedComment))
        {
            return null;
        }

        var normalizedUser = user.Trim();
        if (string.IsNullOrWhiteSpace(normalizedUser))
        {
            throw new InvalidOperationException("A logged-on user is required to save an alert comment.");
        }

        if (normalizedUser.Length > 256)
        {
            throw new InvalidOperationException("The username may contain at most 256 characters.");
        }

        return new LogbookEntry
        {
            Id = Guid.NewGuid(),
            CreatedAt = createdAt,
            User = normalizedUser,
            Comment = $"Alert comment: {normalizedComment}\nAlert: {Display(alert.Name)}\nTarget: {Display(alert.TargetDisplayName)}"
        };
    }

    private static string Display(string value) =>
        string.IsNullOrWhiteSpace(value) ? "(unknown)" : value.Trim();
}
