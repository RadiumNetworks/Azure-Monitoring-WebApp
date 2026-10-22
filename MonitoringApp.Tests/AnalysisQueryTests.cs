using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;

namespace MonitoringApp.Tests;

public sealed class AnalysisQueryTests
{
    private const string ValidKql =
        """
        Perf
        | where Computer in~ (_SelectedSystems)
        | summarize Value = avg(CounterValue) by Timestamp = bin(TimeGenerated, _BinSize), Series = Computer
        | project Timestamp, Series, Value
        """;
    private const string WorkspaceKql =
        """
        SysvolStats_CL
        | where TimeGenerated > ago(_TimePeriod)
        | summarize arg_max(TimeGenerated, *) by Environment
        | project Series = tostring(Environment), Value = todouble(LargeGPOs)
        """;

    [Fact]
    public void AcceptsSafeTimeSeriesDefinition()
    {
        var query = Definition();

        Assert.Empty(AnalysisQueryValidator.ValidateDefinition(query));
        Assert.Empty(AnalysisQueryValidator.ValidateKql(query.Kql));
    }

    [Theory]
    [InlineData("workspace(\"other\").Perf | where Computer in~ (_SelectedSystems) | summarize Value=count() by Timestamp=bin(TimeGenerated, _BinSize), Series=Computer")]
    [InlineData("cluster(\"other\").database(\"db\").Perf | where Computer in~ (_SelectedSystems) | summarize Value=count() by Timestamp=bin(TimeGenerated, _BinSize), Series=Computer")]
    [InlineData(".show tables\nlet _SelectedSystems=dynamic([]); let _BinSize=5m;")]
    public void RejectsExplicitCrossWorkspaceAndManagementKql(string kql)
    {
        var errors = AnalysisQueryValidator.ValidateKql(kql);

        Assert.Contains(errors, error => error.Contains("not allowed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PublishedDefinitionUsesImmutableVersionSnapshot()
    {
        var query = Definition();
        var versionId = Guid.NewGuid();
        query.Name = "Changed draft";
        query.Status = AnalysisQueryStatuses.Draft;
        query.PublishedVersionId = versionId;
        query.Versions.Add(new AnalysisQueryVersion
        {
            Id = versionId,
            Version = 2,
            SnapshotJson = JsonSerializer.Serialize(new AnalysisQuerySnapshot(
                "cpu-usage",
                "Published CPU",
                "Published description",
                "Performance",
                ValidKql,
                AnalysisQueryVisualizationTypes.LineChart,
                "Percent",
                60,
                1440,
                5,
                SqlAuthenticationRoles.Operator))
        });

        var published = AnalysisQueryRegistry.CreatePublishedDefinition(query);

        Assert.Equal("Published CPU", published.Name);
        Assert.Equal(2, published.Version);
        Assert.Equal(AnalysisQueryStatuses.Published, published.Status);
    }

    [Fact]
    public void EnforcesMinimumApplicationRole()
    {
        var reader = Principal(SqlAuthenticationRoles.Reader);
        var admin = Principal(SqlAuthenticationRoles.Admin);

        Assert.False(AnalysisQueryExecutionService.IsRoleAllowed(
            SqlAuthenticationRoles.Operator,
            reader,
            openAuthentication: false));
        Assert.True(AnalysisQueryExecutionService.IsRoleAllowed(
            SqlAuthenticationRoles.Operator,
            admin,
            openAuthentication: false));
        Assert.True(AnalysisQueryExecutionService.IsRoleAllowed(
            SqlAuthenticationRoles.Admin,
            reader,
            openAuthentication: true));
    }

    [Fact]
    public void BuildsEffectiveKqlWithJsonEncodedSystemsAndServerBin()
    {
        var kql = AnalysisQueryRunner.BuildEffectiveKql(
            ValidKql,
            ["SERVER01", "SERVER\"02"],
            15);

        Assert.StartsWith(
            "let _SelectedSystems = dynamic([\"SERVER01\",\"SERVER\\u002202\"]);\nlet _BinSize = 15m;",
            kql);
        Assert.EndsWith(ValidKql, kql);
    }

    [Fact]
    public void PreviewsWorkspaceRoutingWithoutResourceIdsOrCredentials()
    {
        var subscription = Guid.NewGuid().ToString();
        var workspace = new CrossTenantLogAnalyticsWorkspaceOptions
        {
            Key = "tenant-a",
            DisplayName = "Production A",
            TenantId = Guid.NewGuid().ToString(),
            WorkspaceId = Guid.NewGuid().ToString(),
            SubscriptionIds = [subscription]
        };
        var options = new CrossTenantLogAnalyticsOptions
        {
            Enabled = true,
            ApplicationClientId = Guid.NewGuid().ToString(),
            ManagedIdentityClientId = Guid.NewGuid().ToString(),
            KeyVaultUri = "https://example.vault.azure.net/",
            ClientSecretName = "secret",
            Workspaces = [workspace]
        };
        var runner = new AnalysisQueryRunner(
            options,
            new UnusedSecretProvider(),
            new TargetWorkspaceResolver(options));

        var preview = Assert.Single(runner.CreateExecutionPreviews(
            Definition(),
            [new ComputerInventoryEntry { SubscriptionId = subscription, Computer = "SERVER01" }],
            60));

        Assert.Equal("tenant-a", preview.WorkspaceKey);
        Assert.Equal("Production A", preview.WorkspaceDisplayName);
        Assert.Equal(["SERVER01"], preview.Systems);
        Assert.DoesNotContain(workspace.WorkspaceId, preview.EffectiveKql);
        Assert.DoesNotContain(workspace.TenantId, preview.EffectiveKql);
    }

    [Fact]
    public void ConfiguresQueryRegistryModel()
    {
        using var context = new AlertDbContext(
            new DbContextOptionsBuilder<AlertDbContext>()
                .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=ModelOnly;Trusted_Connection=True;")
                .Options);
        var definition = Assert.IsAssignableFrom<Microsoft.EntityFrameworkCore.Metadata.IEntityType>(
            context.Model.FindEntityType(typeof(AnalysisQueryDefinition)));
        Assert.Equal("AnalysisQueryDefinitions", definition.GetTableName());
        Assert.True(definition.FindProperty(nameof(AnalysisQueryDefinition.RowVersion))?.IsConcurrencyToken);
        Assert.Contains(definition.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual([nameof(AnalysisQueryDefinition.Key)]));
    }

    [Fact]
    public void PreviewsUnsavedDraftWithSelectedWorkspaceAndSystems()
    {
        var (options, runner, systems) = DraftEnvironment();
        var query = DraftDefinition(options);
        query.Id = Guid.Empty;

        var preview = runner.CreateDraftExecutionPreview(query, "tenant-a", systems, 60);

        Assert.Equal("tenant-a", preview.WorkspaceKey);
        Assert.Equal(["SERVER01", "SERVER02"], preview.Systems);
        Assert.Contains("dynamic([\"SERVER01\",\"SERVER02\"])", preview.EffectiveKql);
        Assert.Equal(AnalysisQueryStatuses.Draft, query.Status);
        Assert.Empty(query.Versions);
    }

    [Theory]
    [InlineData("unknown", 60, "configured")]
    [InlineData("tenant-a", 4, "Time range")]
    [InlineData("tenant-a", 1441, "Time range")]
    public void RejectsInvalidDraftTestSelection(string workspaceKey, int minutes, string message)
    {
        var (options, runner, systems) = DraftEnvironment();

        var error = Assert.Throws<InvalidOperationException>(() =>
            runner.CreateDraftExecutionPreview(DraftDefinition(options), workspaceKey, systems, minutes));

        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void RejectsSystemsFromAnotherWorkspace()
    {
        var (options, runner, _) = DraftEnvironment();
        var system = new ComputerInventoryEntry
        {
            SubscriptionId = options.Workspaces[1].SubscriptionIds[0],
            Computer = "OTHER"
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            runner.CreateDraftExecutionPreview(DraftDefinition(options), "tenant-a", [system], 60));

        Assert.Contains("route to the selected workspace", error.Message);
    }

    [Fact]
    public void RejectsDisabledWorkspaceAndUnsafeDraftKql()
    {
        var (options, runner, systems) = DraftEnvironment();
        var query = DraftDefinition(options);
        query.Workspaces.First().Enabled = false;
        Assert.Contains("Enable the selected workspace", Assert.Throws<InvalidOperationException>(() =>
            runner.CreateDraftExecutionPreview(query, "tenant-a", systems, 60)).Message);

        query.Workspaces.First().Enabled = true;
        query.Kql = "workspace('other')." + ValidKql;
        Assert.Contains("not allowed", Assert.Throws<InvalidOperationException>(() =>
            runner.CreateDraftExecutionPreview(query, "tenant-a", systems, 60)).Message);
    }

    [Fact]
    public async Task DisabledLiveAnalyticsDoesNotAccessCredentials()
    {
        var options = new CrossTenantLogAnalyticsOptions();
        var runner = new AnalysisQueryRunner(options, new UnusedSecretProvider(), new TargetWorkspaceResolver(options));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.ExecuteDraftAsync(Definition(), "tenant-a", [], 60));

        Assert.Contains("disabled", error.Message);
    }

    [Fact]
    public async Task SavesWorkspaceSelectionWithoutEnablingUntestedWorkspaces()
    {
        var (options, runner, _) = DraftEnvironment();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new TestDbContextFactory(new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection).Options);
        await using (var context = factory.CreateDbContext())
        {
            var names = new[] { "AnalysisQueryDefinitions", "AnalysisQueryWorkspaces", "AnalysisQueryVersions", "ComputerInventory" };
            foreach (var statement in context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(statement => names.Any(name => statement.TrimStart().StartsWith($"CREATE TABLE \"{name}\" ", StringComparison.Ordinal))))
            {
                await context.Database.ExecuteSqlRawAsync(statement
                    .Replace("{}", "{{}}", StringComparison.Ordinal)
                    .Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal)
                    .Replace("\"RowVersion\" BLOB NOT NULL", "\"RowVersion\" BLOB NOT NULL DEFAULT X''", StringComparison.Ordinal));
            }
        }
        var registry = new AnalysisQueryRegistry(factory, options, runner);
        var draft = DraftDefinition(options);
        draft.Id = Guid.Empty;
        draft.VisualizationJson = """{"color":"purple","columns":[{"name":"Value","decimals":3}]}""";
        draft.Workspaces.Last().Enabled = false;

        var saved = await registry.SaveDraftAsync(draft, "admin");
        Assert.NotEqual(Guid.Empty, saved.Id);
        Assert.True(saved.Workspaces.Single(item => item.WorkspaceKey == "tenant-a").Enabled);
        Assert.False(saved.Workspaces.Single(item => item.WorkspaceKey == "tenant-b").Enabled);
        await using (var context = factory.CreateDbContext())
        {
            var assignment = await context.AnalysisQueryWorkspaces.SingleAsync(item => item.WorkspaceKey == "tenant-a");
            assignment.LastValidationStatus = AnalysisQueryValidationStatuses.Succeeded;
            assignment.LastValidatedAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync();
        }

        saved.Kql += "\n| where Value >= 0";
        await registry.SaveDraftAsync(saved, "admin");
        var unchangedAssignment = Assert.Single(await registry.ListAsync()).Workspaces
            .Single(item => item.WorkspaceKey == "tenant-a");
        Assert.Equal(AnalysisQueryValidationStatuses.Succeeded, unchangedAssignment.LastValidationStatus);
        Assert.NotNull(unchangedAssignment.LastValidatedAt);

        saved.Workspaces.First(item => item.WorkspaceKey == "tenant-a").Enabled = false;
        saved.Workspaces.First(item => item.WorkspaceKey == "tenant-b").Enabled = true;
        await registry.SaveDraftAsync(saved, "admin");
        var reloaded = Assert.Single(await registry.ListAsync());

        Assert.False(reloaded.Workspaces.Single(item => item.WorkspaceKey == "tenant-a").Enabled);
        Assert.True(reloaded.Workspaces.Single(item => item.WorkspaceKey == "tenant-b").Enabled);
        var newlyEnabled = reloaded.Workspaces.Single(item => item.WorkspaceKey == "tenant-b");
        Assert.Equal(AnalysisQueryValidationStatuses.NotValidated, newlyEnabled.LastValidationStatus);
        Assert.Null(newlyEnabled.LastValidatedAt);
        Assert.Equal(AnalysisQueryStatuses.Draft, reloaded.Status);
        Assert.Empty(reloaded.Versions);

        var missingSystemError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.TestDraftAsync(reloaded, "tenant-b", [new("missing", "missing")], 60));
        Assert.Contains("not present in inventory", missingSystemError.Message);
        Assert.Single(await registry.ListAsync());

        await using (var context = factory.CreateDbContext())
        {
            var query = await context.AnalysisQueryDefinitions.SingleAsync();
            var version = new AnalysisQueryVersion
            {
                Id = Guid.NewGuid(), QueryDefinitionId = query.Id, Version = 1,
                SnapshotJson = JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(query))
            };
            context.AnalysisQueryVersions.Add(version);
            query.PublishedVersionId = version.Id;
            query.Enabled = true;
            await context.SaveChangesAsync();
        }
        Assert.Single(await registry.ListPublishedAsync());
        await registry.DisableAsync(reloaded.Id, "admin");
        Assert.Empty(await registry.ListPublishedAsync());
        var disabled = Assert.Single(await registry.ListAsync());
        Assert.False(disabled.Enabled);
        Assert.Equal(AnalysisQueryStatuses.Disabled, disabled.Status);
        Assert.Equal("admin", disabled.UpdatedBy);
        Assert.Single(disabled.Versions);
        var execution = new AnalysisQueryExecutionService(factory, runner);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => execution.ExecuteAsync(
            new AnalysisQueryRequest(reloaded.Id, [new("subscription", "SERVER01")], 60)));
    }

    [Theory]
    [InlineData(true, AnalysisQueryScopes.SelectedSystems)]
    [InlineData(false, AnalysisQueryScopes.SelectedSystems)]
    [InlineData(true, AnalysisQueryScopes.Workspace)]
    [InlineData(false, AnalysisQueryScopes.Workspace)]
    public async Task UnpublishWithdrawsExecutionRetainsHistoryAndAllowsNextVersion(bool enabled, string scope)
    {
        var (options, runner, _) = DraftEnvironment();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new TestDbContextFactory(new DbContextOptionsBuilder<AlertDbContext>().UseSqlite(connection).Options);
        await using (var context = factory.CreateDbContext())
        {
            var names = new[] { "AnalysisQueryDefinitions", "AnalysisQueryWorkspaces", "AnalysisQueryVersions" };
            foreach (var statement in context.Database.GenerateCreateScript().Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(statement => names.Any(name => statement.TrimStart().StartsWith($"CREATE TABLE \"{name}\" ", StringComparison.Ordinal))))
            {
                await context.Database.ExecuteSqlRawAsync(statement
                    .Replace("{}", "{{}}", StringComparison.Ordinal)
                    .Replace("nvarchar(max)", "TEXT", StringComparison.Ordinal)
                    .Replace("\"RowVersion\" BLOB NOT NULL", "\"RowVersion\" BLOB NOT NULL DEFAULT X''", StringComparison.Ordinal));
            }
        }
        var registry = new AnalysisQueryRegistry(factory, options, runner);
        var draft = DraftDefinition(options);
        draft.Id = Guid.Empty;
        draft.VisualizationJson = """{"color":"purple","columns":[{"name":"Value","decimals":3}]}""";
        draft.Scope = scope;
        if (scope == AnalysisQueryScopes.Workspace)
        {
            draft.Kql = WorkspaceKql;
            draft.VisualizationType = AnalysisQueryVisualizationTypes.Table;
        }
        var saved = await registry.SaveDraftAsync(draft, "admin");
        await using (var context = factory.CreateDbContext())
        {
            var query = await context.AnalysisQueryDefinitions.Include(item => item.Workspaces).SingleAsync();
            query.Status = AnalysisQueryStatuses.Validated;
            foreach (var workspace in query.Workspaces)
                workspace.LastValidationStatus = AnalysisQueryValidationStatuses.Succeeded;
            await context.SaveChangesAsync();
        }
        await registry.PublishAsync(saved.Id, "publisher");
        Assert.Equal("purple", AnalysisVisualization.Parse(Assert.Single(await registry.ListPublishedAsync()).VisualizationJson).Color);
        Assert.Equal(scope, Assert.Single(await registry.ListPublishedAsync()).Scope);
        if (!enabled)
            await registry.DisableAsync(saved.Id, "admin");
        var before = Assert.Single(await registry.ListAsync());
        var originalSnapshot = Assert.Single(before.Versions).SnapshotJson;
        Assert.Equal(draft.VisualizationJson, before.VisualizationJson);

        await registry.UnpublishAsync(saved.Id, "unpublisher");

        var withdrawn = Assert.Single(await registry.ListAsync());
        Assert.Null(withdrawn.PublishedVersionId);
        Assert.Equal(enabled, withdrawn.Enabled);
        Assert.Equal(enabled ? AnalysisQueryStatuses.Draft : AnalysisQueryStatuses.Disabled, withdrawn.Status);
        Assert.Equal(1, withdrawn.Version);
        Assert.Equal(originalSnapshot, Assert.Single(withdrawn.Versions).SnapshotJson);
        Assert.Equal(before.Kql, withdrawn.Kql);
        Assert.Equal(before.VisualizationJson, withdrawn.VisualizationJson);
        Assert.Equal(scope, withdrawn.Scope);
        Assert.Equal("unpublisher", withdrawn.UpdatedBy);
        Assert.True(withdrawn.UpdatedAt >= before.UpdatedAt);
        Assert.Empty(await registry.ListPublishedAsync());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => new AnalysisQueryExecutionService(factory, runner)
            .ExecuteAsync(new(saved.Id, [new("subscription", "DC01")], 60)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.UnpublishAsync(saved.Id, "admin"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.PublishAsync(saved.Id, "publisher"));

        await using (var context = factory.CreateDbContext())
        {
            var query = await context.AnalysisQueryDefinitions.SingleAsync();
            query.Status = AnalysisQueryStatuses.Validated;
            await context.SaveChangesAsync();
        }
        var republished = await registry.PublishAsync(saved.Id, "publisher");
        Assert.True(republished.Enabled);
        Assert.Equal(2, republished.Version);
        Assert.NotEqual(before.PublishedVersionId, republished.PublishedVersionId);
        Assert.Equal(2, Assert.Single(await registry.ListAsync()).Versions.Count);
        Assert.Single(await registry.ListPublishedAsync());
    }

    [Fact]
    public void WorkspaceScopeIsExplicitAndDoesNotDisableKqlGuards()
    {
        Assert.NotEmpty(AnalysisQueryValidator.ValidateKql(WorkspaceKql, AnalysisQueryVisualizationTypes.Table));
        Assert.Empty(AnalysisQueryValidator.ValidateKql(WorkspaceKql, AnalysisQueryVisualizationTypes.Table, AnalysisQueryScopes.Workspace));
        Assert.NotEmpty(AnalysisQueryValidator.ValidateKql(WorkspaceKql, AnalysisQueryVisualizationTypes.Table, "Unknown"));
        Assert.NotEmpty(AnalysisQueryValidator.ValidateKql(WorkspaceKql.Replace("_TimePeriod", "1h"), AnalysisQueryVisualizationTypes.Table, AnalysisQueryScopes.Workspace));
        Assert.NotEmpty(AnalysisQueryValidator.ValidateKql(WorkspaceKql, AnalysisQueryVisualizationTypes.StatusHistory, AnalysisQueryScopes.Workspace));
        foreach (var prefix in new[] { ".show tables\n", "workspace('other').", "cluster('other').database('db')." })
            Assert.Contains(AnalysisQueryValidator.ValidateKql(prefix + WorkspaceKql, AnalysisQueryVisualizationTypes.Table, AnalysisQueryScopes.Workspace),
                error => error.Contains("not allowed", StringComparison.Ordinal));
    }

    [Fact]
    public void WorkspaceScopeRoutesOnlySelectedWorkspacesOnceAndDoesNotFilterDomainsByComputer()
    {
        var (options, runner, systems) = DraftEnvironment();
        var query = DraftDefinition(options);
        query.Scope = AnalysisQueryScopes.Workspace;
        query.Kql = WorkspaceKql;
        query.VisualizationType = AnalysisQueryVisualizationTypes.Table;
        var other = new ComputerInventoryEntry { Computer = "SERVER03", SubscriptionId = options.Workspaces[1].SubscriptionIds[0] };
        var previews = runner.CreateExecutionPreviews(query, [.. systems, other], 60);
        Assert.Equal(2, previews.Count);
        Assert.Equal(["tenant-a", "tenant-b"], previews.Select(preview => preview.WorkspaceKey));
        Assert.Equal(["SERVER01", "SERVER02"], previews[0].Systems);
        Assert.Equal(["SERVER03"], previews[1].Systems);
        Assert.All(previews, preview => Assert.EndsWith(WorkspaceKql, preview.EffectiveKql));
        Assert.DoesNotContain("Computer", query.Kql);
        Assert.DoesNotContain("_SelectedSystems", query.Kql);
        Assert.Single(runner.CreateExecutionPreviews(query, systems, 60));
        Assert.Equal("tenant-a", runner.CreateDraftExecutionPreview(query, "tenant-a", systems, 60).WorkspaceKey);
        query.Workspaces.First().Enabled = false;
        Assert.Throws<InvalidOperationException>(() => runner.CreateDraftExecutionPreview(query, "tenant-a", systems, 60));
    }

    [Theory]
    [InlineData(false, AnalysisQueryValidationStatuses.Succeeded)]
    [InlineData(true, AnalysisQueryValidationStatuses.NotValidated)]
    [InlineData(true, AnalysisQueryValidationStatuses.Failed)]
    public async Task WorkspaceScopeStillRequiresEnabledAndValidatedWorkspaces(bool enabled, string validation)
    {
        var (options, runner, systems) = DraftEnvironment();
        var query = DraftDefinition(options);
        query.Scope = AnalysisQueryScopes.Workspace;
        query.Kql = WorkspaceKql;
        query.VisualizationType = AnalysisQueryVisualizationTypes.Table;
        query.Workspaces.First().Enabled = enabled;
        query.Workspaces.First().LastValidationStatus = validation;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ExecuteAsync(query, systems, 60));
        Assert.Contains("not validated", error.Message);
    }

    [Fact]
    public void ScopeIsVersionedAndLegacySnapshotsRemainSystemScoped()
    {
        var query = Definition();
        query.Scope = AnalysisQueryScopes.Workspace;
        query.Kql = WorkspaceKql;
        var version = new AnalysisQueryVersion
        {
            Id = Guid.NewGuid(), Version = 1,
            SnapshotJson = JsonSerializer.Serialize(AnalysisQueryRegistry.CreateSnapshot(query))
        };
        query.PublishedVersionId = version.Id;
        query.Versions.Add(version);
        query.Scope = AnalysisQueryScopes.SelectedSystems;
        Assert.Equal(AnalysisQueryScopes.Workspace, AnalysisQueryRegistry.CreatePublishedDefinition(query).Scope);
        var json = JsonSerializer.SerializeToNode(AnalysisQueryRegistry.CreateSnapshot(query))!.AsObject();
        json.Remove("Scope");
        version.SnapshotJson = json.ToJsonString();
        query.Scope = AnalysisQueryScopes.Workspace;
        Assert.Equal(AnalysisQueryScopes.SelectedSystems, AnalysisQueryRegistry.CreatePublishedDefinition(query).Scope);
        query.Scope = "Unknown";
        Assert.Contains(AnalysisQueryValidator.ValidateDefinition(query), error => error.Contains("scope", StringComparison.Ordinal));
    }

    private static (CrossTenantLogAnalyticsOptions Options, AnalysisQueryRunner Runner, ComputerInventoryEntry[] Systems) DraftEnvironment()
    {
        var subscription = Guid.NewGuid().ToString();
        var options = new CrossTenantLogAnalyticsOptions
        {
            Enabled = true,
            Workspaces =
            [
                new() { Key = "tenant-a", DisplayName = "Workspace A", SubscriptionIds = [subscription] },
                new() { Key = "tenant-b", DisplayName = "Workspace B", SubscriptionIds = [Guid.NewGuid().ToString()] }
            ]
        };
        var runner = new AnalysisQueryRunner(options, new UnusedSecretProvider(), new TargetWorkspaceResolver(options));
        return (options, runner,
        [
            new() { SubscriptionId = subscription, Computer = "SERVER02" },
            new() { SubscriptionId = subscription, Computer = "SERVER01" }
        ]);
    }

    private static AnalysisQueryDefinition DraftDefinition(CrossTenantLogAnalyticsOptions options)
    {
        var query = Definition();
        query.Workspaces = options.Workspaces.Select(workspace => new AnalysisQueryWorkspace
        {
            WorkspaceKey = workspace.Key,
            Enabled = true
        }).ToList();
        return query;
    }

    private sealed class TestDbContextFactory(DbContextOptions<AlertDbContext> options) : IDbContextFactory<AlertDbContext>
    {
        public AlertDbContext CreateDbContext() => new(options);
    }

    private static AnalysisQueryDefinition Definition() => new()
    {
        Id = Guid.NewGuid(),
        Key = "cpu-usage",
        Name = "CPU usage",
        Category = "Performance",
        Kql = ValidKql,
        Unit = "Percent",
        DefaultTimeRangeMinutes = 60,
        MaxTimeRangeMinutes = 1440,
        DefaultBinMinutes = 5,
        MinimumRole = SqlAuthenticationRoles.Operator
    };

    private static ClaimsPrincipal Principal(string role) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test"));

    private sealed class UnusedSecretProvider : ILogAnalyticsSecretProvider
    {
        public Task<string> GetClientSecretAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The preview must not access Key Vault.");
    }
}
