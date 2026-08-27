using System.Text.Json;
using System.Text.Json.Nodes;

namespace MonitoringApp.Tests;

internal static class TestAlertFactory
{
    public static AlertRecord FromCommonPayload(JsonObject payload)
    {
        var essentials = payload["data"]?["essentials"]?.AsObject()
            ?? throw new InvalidOperationException("Alert essentials are required.");
        var firedAt = DateTimeOffset.Parse(essentials["firedDateTime"]!.GetValue<string>());
        var target = essentials["alertTargetIDs"]?.AsArray()[0]?.GetValue<string>() ?? string.Empty;
        return new AlertRecord(
            Guid.NewGuid(), firedAt, GetAlertId(payload), Value(essentials, "alertRule"),
            Value(essentials, "severity"), string.Empty, Value(essentials, "signalType"),
            Value(essentials, "monitorCondition"), target, Value(essentials, "targetResourceGroup"),
            Value(essentials, "targetSubscriptionId"), firedAt, Value(essentials, "description"),
            string.Empty, string.Empty, Serialize(payload));
    }

    public static AlertRecord FromEvent(AlertEventCase source, AlertRecordDefaults defaults) => new(
        Guid.NewGuid(), source.ReceivedAt, source.AlertId, defaults.Name, defaults.Severity, string.Empty,
        defaults.SignalType, source.Condition, OrDefault(source.Target, defaults.Target),
        OrDefault(source.ResourceGroup, defaults.ResourceGroup), OrDefault(source.SubscriptionId, defaults.SubscriptionId),
        source.ReceivedAt, defaults.Description, string.Empty,
        source.Comments, "{}");

    public static AlertRecord FromPrompt(PromptAlertCase source) => new(
        Guid.NewGuid(), source.ReceivedAt, source.AlertId, source.Name, source.Severity, string.Empty,
        source.SignalType, source.Condition, source.Target, source.ResourceGroup, source.SubscriptionId,
        source.FiredAt, source.Description, source.SearchResultsUrl, source.Comments, Serialize(source.Payload));

    public static AlertRecord WithPayload(JsonObject payload, AlertRecordDefaults defaults) => new(
        Guid.NewGuid(), DateTimeOffset.UnixEpoch, string.Empty, defaults.Name, defaults.Severity, string.Empty,
        defaults.SignalType, string.Empty, defaults.Target, defaults.ResourceGroup, defaults.SubscriptionId,
        DateTimeOffset.UnixEpoch, defaults.Description, string.Empty, string.Empty, Serialize(payload));

    public static string GetAlertId(JsonObject payload) =>
        payload["data"]?["essentials"]?["alertId"]?.GetValue<string>()
        ?? throw new InvalidOperationException("Alert ID is required.");

    private static string Value(JsonObject source, string name) => source[name]?.GetValue<string>() ?? string.Empty;

    private static string OrDefault(string value, string fallback) =>
        string.IsNullOrEmpty(value) ? fallback : value;

    private static string Serialize(JsonObject payload) =>
        payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
}