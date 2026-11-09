namespace MonitoringApp.Tests;

public sealed class AnalysisDashboardQueryBatchTests
{
    [Fact]
    public async Task EquivalentWidgetsShareOneInFlightTaskIncludingImplicitDefaultBins()
    {
        var query = Definition();
        var request = Request(query);
        var gate = new TaskCompletionSource<AnalysisQueryResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var batch = new AnalysisDashboardQueryBatch((_, _) => { calls++; return gate.Task; }, default);

        var first = batch.ExecuteAsync(query, request);
        var second = batch.ExecuteAsync(query, request with { BinMinutes = query.DefaultBinMinutes });

        Assert.Same(first, second);
        Assert.Equal(1, calls);
        Assert.Equal(1, batch.Count);
        var response = Response(query.Id);
        gate.SetResult(response);
        Assert.Same(response, await first);
        Assert.Same(response, await second);
    }

    [Theory]
    [InlineData("query")]
    [InlineData("version")]
    [InlineData("published-version")]
    [InlineData("default-bin")]
    [InlineData("time")]
    [InlineData("bin")]
    [InlineData("group")]
    [InlineData("workspace")]
    [InlineData("domain")]
    [InlineData("site")]
    [InlineData("role")]
    [InlineData("system")]
    [InlineData("subscription")]
    [InlineData("order")]
    public async Task EveryEffectiveContextDimensionPreventsIncorrectCoalescing(string change)
    {
        var query = Definition();
        var request = Request(query);
        var calls = 0;
        var batch = new AnalysisDashboardQueryBatch(
            (_, _) => { calls++; return Task.FromResult(Response(query.Id)); }, default);
        await batch.ExecuteAsync(query, request);
        var other = request;
        switch (change)
        {
            case "query":
                query.Id = Guid.NewGuid();
                other = request with { QueryDefinitionId = query.Id };
                break;
            case "version": query.Version++; break;
            case "published-version": query.PublishedVersionId = Guid.NewGuid(); break;
            case "default-bin": query.DefaultBinMinutes = 30; break;
            case "time": other = request with { TimeRangeMinutes = 240 }; break;
            case "bin": other = request with { BinMinutes = 30 }; break;
            case "group": other = request with { GroupBy = "NC" }; break;
            case "workspace": other = request with { WorkspaceKey = "other" }; break;
            case "domain": other = request with { Domain = "other.example" }; break;
            case "site": other = request with { Site = "other" }; break;
            case "role": other = request with { Role = "other" }; break;
            case "system":
                other = request with { Systems = [new("sub1", "other"), request.Systems[1]] };
                break;
            case "subscription":
                other = request with { Systems = [new("other", "DC01"), request.Systems[1]] };
                break;
            case "order": other = request with { Systems = request.Systems.Reverse().ToArray() }; break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }

        await batch.ExecuteAsync(query, other);

        Assert.Equal(2, calls);
        Assert.Equal(2, batch.Count);
    }

    [Fact]
    public async Task FilterBoundariesCannotCollideThroughDelimiterCharacters()
    {
        var query = Definition();
        var calls = 0;
        var batch = new AnalysisDashboardQueryBatch(
            (_, _) => { calls++; return Task.FromResult(Response(query.Id)); }, default);
        var request = Request(query);

        await batch.ExecuteAsync(query, request with { WorkspaceKey = "a|b", Domain = "c" });
        await batch.ExecuteAsync(query, request with { WorkspaceKey = "a", Domain = "b|c" });

        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FaultsStayVisibleAndAreSharedForEquivalentWidgets(bool synchronous)
    {
        var query = Definition();
        var request = Request(query);
        var calls = 0;
        var batch = new AnalysisDashboardQueryBatch((_, _) =>
        {
            calls++;
            var failure = new InvalidOperationException("Visible query failure.");
            return synchronous ? throw failure : Task.FromException<AnalysisQueryResponse>(failure);
        }, default);

        var first = batch.ExecuteAsync(query, request);
        var second = batch.ExecuteAsync(query, request);

        Assert.Same(first, second);
        Assert.Equal("Visible query failure.", (await Assert.ThrowsAsync<InvalidOperationException>(() => first)).Message);
        Assert.Equal("Visible query failure.", (await Assert.ThrowsAsync<InvalidOperationException>(() => second)).Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SeparateRefreshBatchesNeverReuseResults()
    {
        var query = Definition();
        var request = Request(query);
        var calls = 0;
        Task<AnalysisQueryResponse> Execute(AnalysisQueryRequest _, CancellationToken token)
        {
            calls++;
            return Task.FromResult(Response(query.Id));
        }

        var first = await new AnalysisDashboardQueryBatch(Execute, default).ExecuteAsync(query, request);
        var second = await new AnalysisDashboardQueryBatch(Execute, default).ExecuteAsync(query, request);

        Assert.NotSame(first, second);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshCancellationPropagatesWithoutReturningAReplacementResult(bool alreadyCancelled)
    {
        var query = Definition();
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var batch = new AnalysisDashboardQueryBatch(async (_, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            calls++;
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("The delay must be cancelled.");
        }, cancellation.Token);
        if (alreadyCancelled)
            cancellation.Cancel();
        else
        {
            var pending = batch.ExecuteAsync(query, Request(query));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batch.ExecuteAsync(query, Request(query)));
        Assert.Equal(alreadyCancelled ? 0 : 1, calls);
    }

    [Fact]
    public async Task ExecutedSystemsAreAnImmutableSnapshotRatherThanAnAliasOfUiState()
    {
        var query = Definition();
        var systems = new List<AnalysisQuerySystem> { new("sub1", "DC01") };
        AnalysisQueryRequest? captured = null;
        var batch = new AnalysisDashboardQueryBatch((request, _) =>
        {
            captured = request;
            return Task.FromResult(Response(query.Id));
        }, default);

        await batch.ExecuteAsync(query, Request(query) with { Systems = systems });
        systems.Clear();

        Assert.NotNull(captured);
        Assert.Equal(new("sub1", "DC01"), Assert.Single(captured.Systems));
        var collection = Assert.IsAssignableFrom<IList<AnalysisQuerySystem>>(captured.Systems);
        Assert.Throws<NotSupportedException>(() => collection.Clear());
    }

    [Fact]
    public void MismatchedQueryIdentityFailsBeforeExecutingOrCaching()
    {
        var query = Definition();
        var batch = new AnalysisDashboardQueryBatch(
            (_, _) => throw new InvalidOperationException("Execution must not be called."), default);

        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            _ = batch.ExecuteAsync(query, Request(query) with { QueryDefinitionId = Guid.NewGuid() });
        });

        Assert.Contains("does not match", error.Message);
        Assert.Equal(0, batch.Count);
    }

    private static AnalysisQueryDefinition Definition() => new()
    {
        Id = Guid.NewGuid(), Version = 1, PublishedVersionId = Guid.NewGuid(), DefaultBinMinutes = 15
    };

    private static AnalysisQueryRequest Request(AnalysisQueryDefinition query) => new(
        query.Id, [new("sub1", "DC01"), new("sub2", "DC02")], 60,
        WorkspaceKey: "primary", Domain: "first.example", Site: "North", Role: "Domain Controller");

    private static AnalysisQueryResponse Response(Guid queryId) => new(
        queryId, "Query", 1, "Count", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch, 0, [], []);
}
