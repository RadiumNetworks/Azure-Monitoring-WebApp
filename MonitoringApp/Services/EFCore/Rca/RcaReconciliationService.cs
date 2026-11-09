using System.Data;
using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

public sealed class RcaReconciliationService(
    IDbContextFactory<AlertDbContext> factory,
    AlertRuleEvaluator evaluator,
    QueryResultPresenter presenter,
    TimeProvider timeProvider)
{
    public Task ReconcileAsync(CancellationToken cancellationToken = default) =>
        DatabaseTransactions.ExecuteAsync(factory, async context =>
        {
            await RcaAutomation.ReconcileAsync(context, evaluator, presenter,
                timeProvider.GetUtcNow(), cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
        }, cancellationToken, IsolationLevel.Serializable);
}
