using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

internal static class AnalysisQueryInventory
{
    public static async Task<ComputerInventoryEntry[]> LoadSelectedAsync(
        AlertDbContext context,
        IReadOnlyList<AnalysisQuerySystem> systems,
        CancellationToken cancellationToken)
    {
        var subscriptions = systems.Select(system => system.SubscriptionId).Distinct().ToArray();
        var computers = systems.Select(system => system.Computer).Distinct().ToArray();
        var inventory = await context.ComputerInventory.AsNoTracking()
            .Where(entry => subscriptions.Contains(entry.SubscriptionId) && computers.Contains(entry.Computer))
            .ToArrayAsync(cancellationToken);
        var selected = systems.Distinct().Select(system => inventory.SingleOrDefault(entry =>
            entry.SubscriptionId.Equals(system.SubscriptionId, StringComparison.OrdinalIgnoreCase) &&
            entry.Computer.Equals(system.Computer, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (selected.Any(entry => entry is null))
            throw new InvalidOperationException("At least one selected system is not present in inventory.");
        return selected.Select(entry => entry!).ToArray();
    }
}
