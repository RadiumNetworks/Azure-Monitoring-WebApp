namespace MonitoringApp;

internal static class SubscriptionComputerKey
{
    public static string Create(string subscriptionId, string computer) =>
        $"{subscriptionId.Trim()}\u001f{computer.Trim()}";
}
