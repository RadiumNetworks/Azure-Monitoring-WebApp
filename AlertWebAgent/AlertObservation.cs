using System.Security.Cryptography;
using System.Text;

namespace AlertWebAgent;

public sealed record AlertObservation(
    string Id,
    string ReceivedAt,
    string Condition,
    string Name,
    string Description,
    string SubscriptionId,
    string TargetName,
    string ResourceGroup,
    string Comments,
    string SearchResultsUrl)
{
    public static AlertObservation Create(
        string receivedAt,
        string condition,
        string name,
        string description,
        string subscriptionId,
        string targetName,
        string resourceGroup,
        string comments,
        string searchResultsUrl)
    {
        var identity = string.Join('\n', receivedAt, condition, name, subscriptionId, targetName, resourceGroup);
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return new AlertObservation(
            id,
            receivedAt.Trim(),
            condition.Trim(),
            name.Trim(),
            description.Trim(),
            subscriptionId.Trim(),
            targetName.Trim(),
            resourceGroup.Trim(),
            NormalizeOptionalValue(comments),
            searchResultsUrl.Trim());
    }

    private static string NormalizeOptionalValue(string value) => value.Trim() == "-" ? string.Empty : value.Trim();
}