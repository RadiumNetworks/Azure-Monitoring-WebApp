using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitoringApp.Tests;

public sealed class AgentAnalysisResultValidatorTests
{
    private static readonly Guid EventId = Guid.Parse("6cd696c8-da9c-47dc-abf7-8bd15777e79c");
    private static readonly Guid RuleId = Guid.Parse("8b4bdd79-a5ac-42a1-a89b-4920182e9f9f");

    [Fact]
    public void AcceptsTraceableResult()
    {
        var errors = AgentAnalysisResultValidator.Validate(
            CreateResult(),
            CreateSnapshot(),
            MicrosoftSourceCatalog.CreateDefault());

        Assert.Empty(errors);
    }

    [Fact]
    public void RejectsUnknownReferencesAndUnsupportedConfirmation()
    {
        var result = CreateResult() with
        {
            TeamsSummary = [new("- **DC-ORION-07** needs review.", ["unknown-detail"])],
            DetailedAnalysis = CreateResult().DetailedAnalysis with
            {
                Hypotheses =
                [
                    new(
                        "hypothesis-1",
                        "RPC is the confirmed root cause.",
                        "Confirmed",
                        [],
                        [EventId],
                        "A directed connectivity test would decide this.")
                ],
                RecommendedChecks =
                [
                    CreateResult().DetailedAnalysis.RecommendedChecks[0] with
                    {
                        EvidenceIds = [Guid.NewGuid()],
                        SourceIds = ["invented-source"]
                    }
                ]
            }
        };

        var errors = AgentAnalysisResultValidator.Validate(
            result,
            CreateSnapshot(),
            MicrosoftSourceCatalog.CreateDefault());

        Assert.Contains(errors, error => error.Contains("unknown detail ID", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("cannot be Confirmed", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("unknown evidence ID", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("unknown or blocked source ID", StringComparison.Ordinal));
    }

    [Fact]
    public void ParsesCanonicalResultAndRejectsUnknownJsonProperties()
    {
        var result = CreateResult();
        var parsed = AgentAnalysisResultParser.ParseAndValidate(
            result.ToCanonicalJson(),
            CreateSnapshot(),
            MicrosoftSourceCatalog.CreateDefault());

        Assert.Equal(result.AnalysisId, parsed.AnalysisId);

        var jsonWithUnknownProperty = result.ToCanonicalJson().Replace(
            "{\"contractVersion\"",
            "{\"unapprovedField\":true,\"contractVersion\"",
            StringComparison.Ordinal);
        var exception = Assert.Throws<AgentAnalysisResultException>(() =>
            AgentAnalysisResultParser.ParseAndValidate(
                jsonWithUnknownProperty,
                CreateSnapshot(),
                MicrosoftSourceCatalog.CreateDefault()));
        Assert.Equal(AgentAnalysisResultCode.SchemaMismatch, exception.Code);
    }

    [Theory]
    [InlineData("null")]
    [InlineData(" \r\nnull ")]
    public void NullResponseKeepsItsExceptionContract(string json)
    {
        var exception = Assert.Throws<AgentAnalysisResultException>(() => AgentAnalysisResultParser.ParseStored(json));
        Assert.Equal(AgentAnalysisResultCode.NullResponse, exception.Code);
        Assert.IsAssignableFrom<InvalidOperationException>(exception);
        Assert.Equal("Agent analysis response contains JSON null.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"contractVersion\":\"SECRET <schema>\"}")]
    public void SchemaMismatchPreservesOriginalJsonException(string json)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        var original = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AgentAnalysisResult>(json, options));
        var exception = Assert.Throws<AgentAnalysisResultException>(() => AgentAnalysisResultParser.ParseStored(json));
        var inner = Assert.IsType<JsonException>(exception.InnerException);
        Assert.IsAssignableFrom<InvalidOperationException>(exception);
        Assert.Equal(AgentAnalysisResultCode.SchemaMismatch, exception.Code);
        Assert.Equal("Agent analysis response does not match the required JSON schema.", exception.Message);
        Assert.Equal(original.Message, inner.Message);
        Assert.Equal(original.Path, inner.Path);
        Assert.Equal(original.LineNumber, inner.LineNumber);
        Assert.Equal(original.BytePositionInLine, inner.BytePositionInLine);
    }

    [Fact]
    public void ValidationFailedKeepsAllNeutralDiagnostics()
    {
        var result = CreateResult() with
        {
            ContractVersion = "custom <contract>",
            AnalysisId = Guid.Empty,
            ResultVersion = 0
        };
        var snapshot = CreateSnapshot();
        var catalog = MicrosoftSourceCatalog.CreateDefault();
        var errors = AgentAnalysisResultValidator.Validate(result, snapshot, catalog);
        var exception = Assert.Throws<AgentAnalysisResultException>(() =>
            AgentAnalysisResultParser.ParseAndValidate(result.ToCanonicalJson(), snapshot, catalog));
        Assert.Equal(AgentAnalysisResultCode.ValidationFailed, exception.Code);
        Assert.IsAssignableFrom<InvalidOperationException>(exception);
        Assert.Equal($"Agent analysis response failed validation: {string.Join(" ", errors)}", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void StoredParsingKeepsCanonicalAndBusinessValidationSemantics()
    {
        var json = CreateResult().ToCanonicalJson();
        Assert.Equal(json, AgentAnalysisResultParser.ParseStored(json).ToCanonicalJson());
        var unsupported = CreateResult() with { ContractVersion = "custom <contract>", ResultVersion = 0 };
        Assert.Equal(unsupported.ToCanonicalJson(),
            AgentAnalysisResultParser.ParseStored(unsupported.ToCanonicalJson()).ToCanonicalJson());
        var caseMismatch = json.Replace("\"contractVersion\":", "\"ContractVersion\":", StringComparison.Ordinal);
        Assert.Equal(AgentAnalysisResultCode.SchemaMismatch,
            Assert.Throws<AgentAnalysisResultException>(() => AgentAnalysisResultParser.ParseStored(caseMismatch)).Code);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node["detailedAnalysis"] = null;
        Assert.Null(AgentAnalysisResultParser.ParseStored(node.ToJsonString()).DetailedAnalysis);
    }

    private static AgentAnalysisResult CreateResult() => new(
        AgentAnalysisResultContract.CurrentVersion,
        Guid.Parse("32948086-e3e2-402e-a09e-a396ec51b486"),
        1,
        [new("- **DC-ORION-07** has an active replication alert.", ["executive-1"])],
        new AgentDetailedAnalysis(
            [new("executive-1", "ObservedFact", "The alert is active.", [EventId], [RuleId], [])],
            [new("hypothesis-1", "Connectivity may be impaired.", "Medium", [EventId], [], "Test directed RPC connectivity.")],
            [new("check-1", "Run a read-only connectivity check.", "DC-ORION-07", "RPC responds or times out.", "Read only.", "Operator access.", "No rollback required.", [EventId], ["ms-ad-rpc-1722"])],
            new AgentOperatorHandoff("operator-handoff", "Investigate", "Replication delay", "Current network trace", "Directory services operator", "[PORTAL_LINK]")));

    private static IncidentAnalysisSnapshot CreateSnapshot() => new(
        new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero),
        new IncidentAnalysisScope(IncidentAnalysisScopeType.Incident, "test"),
        new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero),
        100,
        120_000,
        [new IncidentAlertEvidence(EventId, "alert-1", IncidentEvidenceTier.Primary, "Selected", TimeSpan.Zero, [], [], DateTimeOffset.UtcNow, null, "Replication", "Sev1", "Metric", "Fired", "subscription-a", "rg-a", "DC-ORION-07", "Northbridge", "corp.example.test", "DC", "Failure", new IncidentRuleDecision(IncidentRuleDecisionType.SystemOutage, IncidentAssessmentOrigin.Rule, RuleId, "System outage", "Outage", true, true, "System outage", false), null)],
        [],
        [],
        [],
        new IncidentGraphContext(AlertGraphLayer.Domain, AlertGraphLayer.Site, AlertGraphLayer.Target, "Primary path", []),
        [],
        [new IncidentRuleEvidence(RuleId, "System outage", "Categorization", "SystemOutage", 1, true, "Replication", "", "AnyMatchingAlert", 1, "", "Outage", true, false, true)],
        [],
        [],
        [],
        []);
}