using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

public static class RcaAutomation
{
    public static async Task ReconcileAsync(
        AlertDbContext context,
        AlertRuleEvaluator evaluator,
        QueryResultPresenter presenter,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var storedAlerts = await context.Alerts.AsNoTracking().ToArrayAsync(cancellationToken);
        var alerts = storedAlerts.Where(alert => !InventoryHeartbeatBatch.IsInventoryHeartbeat(alert))
            .Select(alert => alert with { DisplayIdentity = presenter.ResolveIdentity(alert) }).ToArray();
        var rules = await context.AlertRules.AsNoTracking().ToArrayAsync(cancellationToken);
        var inventory = await context.ComputerInventory.AsNoTracking().ToArrayAsync(cancellationToken);
        var active = AlertLifecycle.GetActiveAlerts(alerts);
        var categories = evaluator.Categorize(active, rules, inventory, active).Categories
            .Where(category => category.IsCritical).ToDictionary(category => category.RuleId);
        var cases = (await context.RootCauseAnalyses.ToListAsync(cancellationToken))
            .OrderBy(rca => rca.CreatedAt).ThenBy(rca => rca.Id).ToList();
        var casesById = cases.ToDictionary(rca => rca.Id);
        var alertsById = alerts.ToDictionary(alert => alert.Id);
        var lifecycles = alerts.GroupBy(LifecycleKey).ToDictionary(group => group.Key, group => group.ToArray());
        var links = await context.RcaAlertLinks.ToListAsync(cancellationToken);
        var linkedEvents = links.Select(link => link.AlertEventId).ToHashSet();
        var owners = new Dictionary<string, RootCauseAnalysis>();
        var inventoryTargets = await context.ParsedAlerts.AsNoTracking()
            .Where(alert => alert.InventorySubscriptionId != null && alert.InventoryComputer != null)
            .Select(alert => new { alert.Id, alert.InventorySubscriptionId, alert.InventoryComputer })
            .ToDictionaryAsync(alert => alert.Id, cancellationToken);
        var systemOwners = new Dictionary<string, List<RootCauseAnalysis>>();
        var newLinks = new List<RcaAlertLink>();

        string? SystemKey(AlertRecord alert)
        {
            inventoryTargets.TryGetValue(alert.Id, out var target);
            var subscription = target?.InventorySubscriptionId ?? alert.SubscriptionId;
            var computer = target?.InventoryComputer ?? alert.TargetName;
            return string.IsNullOrWhiteSpace(subscription) || string.IsNullOrWhiteSpace(computer)
                ? null
                : $"{subscription.Trim().ToUpperInvariant()}\0{computer.Trim().ToUpperInvariant()}";
        }

        void RegisterSystem(AlertRecord alert, RootCauseAnalysis rca)
        {
            if (SystemKey(alert) is not { } key)
                return;
            if (!systemOwners.TryGetValue(key, out var existing))
                systemOwners.Add(key, existing = []);
            if (!existing.Contains(rca))
                existing.Add(rca);
        }

        void LinkLifecycle(AlertRecord alert, RootCauseAnalysis rca)
        {
            owners[LifecycleKey(alert)] = rca;
            foreach (var related in lifecycles[LifecycleKey(alert)])
            {
                RegisterSystem(related, rca);
                if (!linkedEvents.Add(related.Id))
                    continue;
                var link = new RcaAlertLink { AlertEventId = related.Id, RcaId = rca.Id, LinkedAt = now };
                context.RcaAlertLinks.Add(link);
                links.Add(link);
                newLinks.Add(link);
            }
        }

        foreach (var link in links)
        {
            var alert = alertsById[link.AlertEventId];
            var rca = casesById[link.RcaId];
            var key = LifecycleKey(alert);
            if (owners.TryGetValue(key, out var existing) && existing.Id != rca.Id)
                throw new InvalidOperationException($"Alert lifecycle {alert.Id} belongs to conflicting RCAs.");
            owners[key] = rca;
            RegisterSystem(alert, rca);
        }

        // Legacy cases retain their history; the oldest case owns any overlapping evidence.
        foreach (var rca in cases)
        {
            var evidenceIds = RcaEvidence.Read(rca.Id, rca.EvidenceEventIdsJson);
            foreach (var id in evidenceIds)
            {
                if (!alertsById.TryGetValue(id, out var alert))
                    continue;
                RegisterSystem(alert, rca);
                if (!owners.ContainsKey(LifecycleKey(alert)))
                    LinkLifecycle(alert, rca);
            }
        }
        foreach (var (key, rca) in owners.ToArray())
            LinkLifecycle(lifecycles[key][0], rca);

        var pendingIds = await context.RcaPrompts.Where(prompt => prompt.Sequence == 1)
            .Select(prompt => prompt.RcaId).ToArrayAsync(cancellationToken);
        var completed = pendingIds.ToHashSet();

        foreach (var category in categories.Values)
        {
            foreach (var alert in category.Alerts.OrderBy(alert => alert.ReceivedAt).ThenBy(alert => alert.Id))
            {
                if (owners.ContainsKey(LifecycleKey(alert)))
                    continue;
                var candidates = SystemKey(alert) is { } key && systemOwners.TryGetValue(key, out var existing)
                    ? existing
                    : [];
                var rca = candidates.Where(candidate => candidate.Status != RcaStatuses.Closed)
                    .OrderBy(candidate => candidate.CreatedAt).ThenBy(candidate => candidate.Id).FirstOrDefault()
                    ?? cases.FirstOrDefault(candidate => candidate.IsActive &&
                        candidate.RuleId == category.RuleId && candidate.Status != RcaStatuses.Closed);
                if (rca is null)
                {
                    rca = new RootCauseAnalysis
                    {
                        Id = Guid.NewGuid(),
                        RuleId = category.RuleId,
                        CategoryName = category.Name,
                        CreatedAt = now,
                        FollowUpDueAt = now.AddMinutes(15),
                        IsActive = true,
                        Revision = 1
                    };
                    context.RootCauseAnalyses.Add(rca);
                    cases.Add(rca);
                    casesById.Add(rca.Id, rca);
                    AddChange(context, rca, now, "System", "Created", "", JsonSerializer.Serialize(new
                    {
                        rca.RuleId, rca.CategoryName, rca.Status, rca.CreatedAt, rca.FollowUpDueAt
                    }));
                }
                LinkLifecycle(alert, rca);
            }
        }

        foreach (var group in newLinks.GroupBy(link => link.RcaId))
        {
            var rca = casesById[group.Key];
            AddChange(context, rca, now, "System", "AlertLinks", "",
                JsonSerializer.Serialize(group.Select(link => link.AlertEventId).Order()));
            rca.Revision++;
        }
        var criticalLifecycles = categories.Values.SelectMany(category => category.Alerts)
            .Select(LifecycleKey).ToHashSet();
        var activeCases = owners.Where(pair => criticalLifecycles.Contains(pair.Key))
            .Select(pair => pair.Value.Id).ToHashSet();
        var linkedIds = links.ToLookup(link => link.RcaId, link => link.AlertEventId);

        foreach (var rca in cases)
        {
            var ids = RcaEvidence.Read(rca.Id, rca.EvidenceEventIdsJson).ToHashSet();
            ids.UnionWith(linkedIds[rca.Id]);
            var isActive = activeCases.Contains(rca.Id);
            if (rca.IsActive != isActive)
            {
                AddChange(context, rca, now, "System", "IsActive",
                    rca.IsActive ? "true" : "false", isActive ? "true" : "false");
                AddChange(context, rca, now, "System", "CriticalEndedAt",
                    rca.CriticalEndedAt?.ToString("O") ?? "", isActive ? "" : now.ToString("O"));
                rca.IsActive = isActive;
                rca.CriticalEndedAt = isActive ? null : now;
                rca.Revision++;
            }
            var evidence = JsonSerializer.Serialize(ids.Order());
            if (rca.EvidenceEventIdsJson != evidence)
            {
                AddChange(context, rca, now, "System", "EvidenceEventIds",
                    rca.EvidenceEventIdsJson, evidence);
                rca.EvidenceEventIdsJson = evidence;
                rca.Revision++;
            }

            var isNew = context.Entry(rca).State == EntityState.Added;
            if (!isNew && (completed.Contains(rca.Id) || now < rca.FollowUpDueAt))
                continue;

            var lifecycleIds = alerts.Where(alert => ids.Contains(alert.Id))
                .Select(alert => alert.AlertId).Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var related = alerts.Where(alert => ids.Contains(alert.Id) || lifecycleIds.Contains(alert.AlertId))
                .OrderBy(alert => alert.ReceivedAt).ToArray();
            if (related.Length == 0 && ids.Count > 0)
                throw new InvalidOperationException($"RCA {rca.Id} has no stored alert evidence.");

            var sequence = isNew ? 0 : 1;
            var rule = rules.FirstOrDefault(rule => rule.Id == rca.RuleId);
            CapturePrompt(context, rca, related, rule, now, sequence, presenter);
        }
    }

    internal static void CapturePrompt(
        AlertDbContext context, RootCauseAnalysis rca, IReadOnlyList<AlertRecord> related,
        AlertRule? rule, DateTimeOffset now, int sequence, QueryResultPresenter presenter)
    {
        var rawPrompt = AgentPromptTemplates.RcaInstructions.Render("\n",
            ("INCIDENT_KIND", rca.RuleId == Guid.Empty ? "manually created" : "critical")) +
            JsonSerializer.Serialize(new
            {
                rca.Id, rca.CategoryName, rca.RuleId, rca.CreatedAt, rca.FollowUpDueAt,
                rca.IsActive, rca.CriticalEndedAt, Sequence = sequence, Rule = rule
            }) + "\n" +
            (related.Count == 0 ? AgentPromptTemplates.RcaNoAlertInstructions.Render("\n") : "") +
            AlertAgentPrompt.Build(related, now, new AlertAgentPromptOptions(), presenter);
        var prompt = new RcaPrompt
        {
            Id = Guid.NewGuid(), RcaId = rca.Id, Sequence = sequence,
            CreatedAt = now, RawPrompt = rawPrompt
        };
        context.RcaPrompts.Add(prompt);
        rca.Revision++;
        AddChange(context, rca, now, "System", "Prompt", "", JsonSerializer.Serialize(new
        {
            prompt.Id, prompt.Sequence, prompt.CreatedAt
        }));
    }

    internal static string LifecycleKey(AlertRecord alert) =>
        RcaEvidence.LifecycleKey(alert);

    public static void AddChange(
        AlertDbContext context, RootCauseAnalysis rca, DateTimeOffset now,
        string actor, string field, string before, string after) =>
        context.RcaChanges.Add(new RcaChange
        {
            Id = Guid.NewGuid(), RcaId = rca.Id, CreatedAt = now,
            Actor = actor, Field = field, Before = before, After = after
        });
}
