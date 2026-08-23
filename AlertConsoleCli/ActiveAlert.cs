namespace AlertConsoleCli;

internal sealed record ActiveAlert(
    string AlertName,
    string Subscription,
    string Target,
    string? SearchResultLink,
    string Status);

internal sealed record ScrapedAlert(ActiveAlert Alert, DateTimeOffset ReceivedAtUtc);
