using System.Text.Json.Nodes;

namespace MonitoringApp.Tests;

public sealed class ScheduledQueryTemplatesTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("scheduled-query-templates-").FullName;

    [Fact]
    public void RuntimeContainsAllTenJsonTemplatesWithoutASourcePathCopy()
    {
        var files = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "AnalysisQueries", "Scheduled"), "*.json");
        Assert.Equal(10, files.Length);
        Assert.False(Directory.Exists(Path.Combine(AppContext.BaseDirectory, "Services", "KQL", "Templates", "Scheduled")));
        foreach (var file in files)
            AssertDisabledDefaults(ScheduledQueryTemplates.Load(file));
    }

    [Fact]
    public void ReloadsEditedMetadataAndKqlAndReturnsIndependentDisabledRules()
    {
        var template = Template();
        var file = Write(template);
        var first = ScheduledQueryTemplates.Load(file);
        first.Enabled = true;
        first.WorkspaceKeys = ["production"];
        template["name"] = "Customized CPU template";
        template["categoryName"] = "Custom category";
        template["kql"] = first.Kql + "\n| take 7";
        template["intervalMinutes"] = 10;
        template["windowMinutes"] = 9;
        template["ingestionDelayMinutes"] = 0;
        template["comparison"] = ">=";
        template["threshold"] = 42;
        template["severity"] = "Sev1";
        Write(template);

        var second = ScheduledQueryTemplates.Load(file);

        AssertDisabledRule(second);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("Customized CPU template", second.Name);
        Assert.Equal("Custom category", second.CategoryName);
        Assert.Equal(first.Kql + "\n| take 7", second.Kql);
        Assert.Equal(10, second.IntervalMinutes);
        Assert.Equal(9, second.WindowMinutes);
        Assert.Equal(0, second.IngestionDelayMinutes);
        Assert.Equal(">=", second.Comparison);
        Assert.Equal(42d, second.Threshold);
        Assert.Equal("Sev1", second.Severity);
        Assert.DoesNotContain("take 7", first.Kql);
        Assert.True(first.Enabled);
        Assert.Equal(["production"], first.WorkspaceKeys);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("categoryName")]
    [InlineData("scope")]
    [InlineData("kql")]
    [InlineData("intervalMinutes")]
    [InlineData("windowMinutes")]
    [InlineData("ingestionDelayMinutes")]
    [InlineData("comparison")]
    [InlineData("threshold")]
    [InlineData("severity")]
    public void RejectsMissingFieldsInsteadOfUsingModelDefaults(string property)
    {
        var template = Template();
        template.Remove(property);
        var file = Write(template);
        var error = Assert.Throws<InvalidOperationException>(() => ScheduledQueryTemplates.Load(file));
        Assert.Contains(file, error.Message);
        Assert.Contains(property, error.Message);
    }

    [Theory]
    [InlineData("name", "null")]
    [InlineData("kql", "null")]
    [InlineData("scope", "null")]
    [InlineData("threshold", "null")]
    [InlineData("name", "\" \"")]
    [InlineData("categoryName", "\"\"")]
    [InlineData("scope", "\"Unknown\"")]
    [InlineData("kql", "\".show tables\"")]
    [InlineData("kql", "\"Heartbeat | take 1\"")]
    [InlineData("intervalMinutes", "0")]
    [InlineData("intervalMinutes", "1441")]
    [InlineData("windowMinutes", "0")]
    [InlineData("windowMinutes", "1441")]
    [InlineData("ingestionDelayMinutes", "-1")]
    [InlineData("ingestionDelayMinutes", "61")]
    [InlineData("comparison", "\"!=\"")]
    [InlineData("threshold", "1e400")]
    [InlineData("severity", "\"Critical\"")]
    [InlineData("enabled", "true")]
    [InlineData("id", "\"00000000-0000-0000-0000-000000000001\"")]
    [InlineData("workspaceKey", "\"production\"")]
    [InlineData("revision", "3")]
    public void RejectsInvalidValuesAndRuntimeStateInTemplates(string property, string json)
    {
        var template = Template();
        template[property] = JsonNode.Parse(json);
        var file = Write(template);
        var error = Assert.Throws<InvalidOperationException>(() => ScheduledQueryTemplates.Load(file));
        Assert.Contains(file, error.Message);
        Assert.NotNull(error.InnerException);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    public void RejectsMalformedOrNonObjectTemplates(string json)
    {
        var file = Path.Combine(directory, "broken.json");
        File.WriteAllText(file, json);
        var error = Assert.Throws<InvalidOperationException>(() => ScheduledQueryTemplates.Load(file));
        Assert.Contains(file, error.Message);
    }

    [Fact]
    public void MissingFileIsAnExplicitErrorWithoutACodeFallback()
    {
        var file = Path.Combine(directory, "missing.json");
        var error = Assert.Throws<InvalidOperationException>(() => ScheduledQueryTemplates.Load(file));
        Assert.Contains(file, error.Message);
        Assert.IsType<FileNotFoundException>(error.InnerException);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResourceExamplesPreserveExactKqlAndDisabledDefaults(bool kerberos)
    {
        var rule = ScheduledQueryRule.Example(kerberos);
        var expected = kerberos
            ? """
              Perf
              | where Computer in~ (_SelectedSystems)
              | where TimeGenerated >= _WindowStart and TimeGenerated < _WindowEnd
              | where ObjectName == "Security System-Wide Statistics" and CounterName == "Kerberos Authentications"
              | summarize Value = avg(CounterValue) by Series = Computer
              """
            : """
              Perf
              | where Computer in~ (_SelectedSystems)
              | where TimeGenerated >= _WindowStart and TimeGenerated < _WindowEnd
              | where ObjectName in ("Processor", "Processor Information") and CounterName == "% Idle Time" and InstanceName == "_Total"
              | summarize Value = 100.0 - avg(CounterValue) by Series = Computer
              """;

        Assert.Equal(expected.ReplaceLineEndings("\n"), rule.Kql.ReplaceLineEndings("\n"));
        Assert.Equal(kerberos ? "Kerberos authentications stopped" : "DC CPU usage above 90%", rule.Name);
        Assert.Equal(kerberos ? "Authentication" : "System Resource", rule.CategoryName);
        Assert.Equal(kerberos ? "=" : ">", rule.Comparison);
        Assert.Equal(kerberos ? 0d : 90d, rule.Threshold);
        Assert.Equal(AnalysisQueryScopes.SelectedSystems, rule.Scope);
        Assert.Equal("Sev2", rule.Severity);
        AssertDisabledDefaults(rule);
        Assert.Empty(AnalysisQueryValidator.ValidateScheduledKql(rule.Kql, rule.Scope));
    }

    [Theory]
    [InlineData(4624, 5, "Successful account logon (4624)", "Account Authentication", "Sev2")]
    [InlineData(4768, 0, "Kerberos TGT request (4768)", "Account Authentication", "Sev2")]
    [InlineData(4769, 0, "Kerberos service ticket request (4769)", "Account Authentication", "Sev2")]
    [InlineData(5136, 3, "Directory object change (5136)", "Account Changes", "Sev2")]
    [InlineData(4724, 0, "Account password reset (4724)", "Account Changes", "Sev2")]
    [InlineData(4736, 0, "Security-enabled local group change (4736)", "Account Changes", "Sev2")]
    [InlineData(4794, 1, "DSRM password reset (4794)", "DSRM Password Reset", "Sev1")]
    public void EventExamplesPreserveExactKqlMetadataAndAccountBounds(
        int eventId, int userIndex, string name, string category, string severity)
    {
        var rule = ScheduledQueryRule.EventExample(eventId);
        var userExpression = eventId is 4724 or 4736
            ? """
              | extend Actor = tostring(eventdata.DataItem.EventData.Data[4]["#text"]),
                       Account = tostring(eventdata.DataItem.EventData.Data[0]["#text"])
              | extend User = iff(isnotempty(Actor) and Actor != "-" and isnotempty(Account) and Account != "-",
                                  strcat(Actor, " changes ", Account), "")
              """
            : $$"""
                | extend User = tostring(eventdata.DataItem.EventData.Data[{{userIndex}}]["#text"])
                | extend User = iff(User == "-", "", User)
                """;
        var expected = $$"""
            let _SecurityEvents = materialize(
                Event
                | where TimeGenerated >= _WindowStart and TimeGenerated < _WindowEnd
                | where EventLog == "Security"
                | project EventID, EventData
            );
            let _Observations = toscalar(_SecurityEvents | count);
            _SecurityEvents
            | where EventID == {{eventId}}
            | extend eventdata = parse_xml(EventData)
            {{userExpression}}
            | summarize MatchingEvents = count(),
                        Users = make_set_if(User, isnotempty(User), 101),
                        UnparsedEvents = countif(isempty(User))
            | project Series = "Workspace",
                      Value = iff(_Observations > 0, todouble(MatchingEvents), real(null)),
                      EventId = {{eventId}},
                      UsersTruncated = array_length(Users) > 100,
                      Users = tostring(array_sort_asc(array_slice(Users, 0, 99))),
                      UnparsedEvents
            """;

        Assert.Equal(expected.ReplaceLineEndings("\n"), rule.Kql.ReplaceLineEndings("\n"));
        Assert.Equal(name, rule.Name);
        Assert.Equal(category, rule.CategoryName);
        Assert.Equal(severity, rule.Severity);
        Assert.Equal(AnalysisQueryScopes.Workspace, rule.Scope);
        Assert.Equal(">", rule.Comparison);
        Assert.Equal(0d, rule.Threshold);
        AssertDisabledDefaults(rule);
        Assert.Empty(AnalysisQueryValidator.ValidateScheduledKql(rule.Kql, rule.Scope));
    }

    [Fact]
    public void SupportedEventsAndPublicLimitsRemainUnchanged()
    {
        Assert.Equal([4624, 4768, 4769, 5136, 4724, 4736, 4794], ScheduledQueryRule.EventIds);
        Assert.Equal(100, ScheduledQueryEventDetails.MaxUsers);
        Assert.Equal(1000, ScheduledQueryService.MaxTargets);
        Assert.Equal(90, ScheduledQueryExecution.QueryTimeoutSeconds);
        Assert.NotEqual(ScheduledQueryRule.Example(false).Id, ScheduledQueryRule.Example(false).Id);
        Assert.NotEqual(ScheduledQueryRule.EventExample(4624).Id, ScheduledQueryRule.EventExample(4624).Id);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4625)]
    [InlineData(65536)]
    public void UnsupportedEventIdsRemainExplicitErrors(int eventId)
    {
        var error = Assert.Throws<ArgumentException>(() => ScheduledQueryRule.EventExample(eventId));
        Assert.Equal("eventId", error.ParamName);
    }

    private static void AssertDisabledDefaults(ScheduledQueryRule rule)
    {
        AssertDisabledRule(rule);
        Assert.Equal(5, rule.IntervalMinutes);
        Assert.Equal(5, rule.WindowMinutes);
        Assert.Equal(2, rule.IngestionDelayMinutes);
    }

    private static void AssertDisabledRule(ScheduledQueryRule rule)
    {
        Assert.NotEqual(Guid.Empty, rule.Id);
        Assert.False(rule.Enabled);
        Assert.Equal(0, rule.Revision);
        Assert.Empty(rule.WorkspaceKeys);
        Assert.Empty(rule.Domain);
        Assert.Empty(rule.Site);
        Assert.Empty(rule.Role);
        Assert.Empty(rule.Computers);
        Assert.Null(rule.LeaseId);
        Assert.Null(rule.LastRunUtc);
    }

    private static JsonObject Template() => JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "AnalysisQueries", "Scheduled", "cpu-high.json")))!.AsObject();

    private string Write(JsonObject template)
    {
        var file = Path.Combine(directory, "template.json");
        File.WriteAllText(file, template.ToJsonString());
        return file;
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
