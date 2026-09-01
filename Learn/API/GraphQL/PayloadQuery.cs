using APILearning.Api;
using APILearning.Data;

namespace APILearning.GraphQL;

public sealed class PayloadQuery
{
    [UseFiltering]
    [UseSorting]
    public async Task<IEnumerable<PayloadRecord>> GetPayloads(
        PayloadStore store,
        CancellationToken cancellationToken) => await store.GetAllAsync(cancellationToken);

    public Task<PayloadRecord?> GetPayload(
        Guid id,
        PayloadStore store,
        CancellationToken cancellationToken) => store.GetAsync(id, cancellationToken);
}