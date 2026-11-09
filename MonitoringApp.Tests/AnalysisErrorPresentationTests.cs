using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace MonitoringApp.Tests;

public sealed class AnalysisErrorPresentationTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public void PublicationErrorsUseWholeMessagesWithoutExposingJsonOrMatchingEnglish(string culture)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            using var services = new ServiceCollection().AddLogging().AddUiLocalization().BuildServiceProvider();
            var text = services.GetRequiredService<UiText>();
            foreach (var code in Enum.GetValues<AnalysisQueryPublicationCode>())
                Assert.Equal(text[$"Analysis.Publication.{code}"], AnalysisErrorPresentation.Message(
                    new AnalysisQueryPublicationException(code, "private publication diagnostic"), text, "Analysis.Errors.ExecutionFailed"));
            Assert.Equal(text["Analysis.Publication.SnapshotInvalid"], AnalysisErrorPresentation.Message(
                new AnalysisQuerySnapshotJsonException(new JsonException("private JSON diagnostic")), text, "Analysis.Errors.ExecutionFailed"));
            foreach (var error in new Exception[]
            {
                new AnalysisQueryPublicationException((AnalysisQueryPublicationCode)int.MaxValue, "private unknown"),
                new InvalidOperationException("The published query snapshot is invalid."),
                new JsonException("private JSON diagnostic"),
                new InvalidOperationException("file wrapper", new AnalysisQuerySnapshotJsonException(new JsonException("private")))
            })
                Assert.Equal(text["Analysis.Errors.ExecutionFailed"],
                    AnalysisErrorPresentation.Message(error, text, "Analysis.Errors.ExecutionFailed"));
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public void TemplateCodesUseWholeResourcesAndNeverTranslateLookalikesOrWrappedDiagnostics(string culture)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            using var services = new ServiceCollection().AddLogging().AddUiLocalization().BuildServiceProvider();
            var text = services.GetRequiredService<UiText>();
            foreach (var code in Enum.GetValues<QueryTemplateValidationCode>())
            {
                var original = new JsonException("private parser diagnostic");
                var exception = new QueryTemplateValidationException(code, "original neutral diagnostic", original);
                Assert.Equal(text[$"QueryTemplate.Validation.{code}"],
                    AnalysisErrorPresentation.Message(exception, text, "Analysis.Errors.ExecutionFailed"));
                Assert.Equal("original neutral diagnostic", exception.Message);
                Assert.Same(original, exception.InnerException);
            }
            foreach (var exception in new Exception[]
            {
                new QueryTemplateValidationException((QueryTemplateValidationCode)int.MaxValue, "private unknown code"),
                new InvalidOperationException("Paste a query JSON document first."),
                new InvalidOperationException("Invalid file template", new QueryTemplateValidationException(
                    QueryTemplateValidationCode.InvalidJson, "private parser diagnostic"))
            })
                Assert.Equal(text["Analysis.Errors.ExecutionFailed"],
                    AnalysisErrorPresentation.Message(exception, text, "Analysis.Errors.ExecutionFailed"));
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public void QuerySchemaIssuesKeepMessagesOrderAndNeutralParameters(string culture)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            using var services = new ServiceCollection().AddLogging().AddUiLocalization().BuildServiceProvider();
            var text = services.GetRequiredService<UiText>();
            AnalysisQueryValidationIssue[] issues =
            [
                new(AnalysisQueryValidationCode.NameInvalid, "original name diagnostic"),
                new(AnalysisQueryValidationCode.WorkspaceTimeRequired, "original KQL diagnostic", "_WindowStart")
            ];
            var exception = new AnalysisQueryValidationException(issues);
            issues[0] = new(AnalysisQueryValidationCode.UnsafeKql, "changed caller issue");
            var expected = text["AnalysisQuery.Validation.NameInvalid"] + " " +
                text.Format("AnalysisQuery.Validation.WorkspaceTimeRequired", "_WindowStart");
            Assert.Equal(expected, AnalysisErrorPresentation.Message(exception, text, "Analysis.Errors.ExecutionFailed"));
            Assert.Equal("original name diagnostic original KQL diagnostic", exception.Message);
            Assert.Equal(AnalysisQueryValidationCode.NameInvalid, exception.Issues[0].Code);
            Assert.Equal("_WindowStart", exception.Issues[1].Parameter);
            Assert.IsAssignableFrom<InvalidOperationException>(exception);
            foreach (var fallback in new Exception[]
            {
                new AnalysisQueryValidationException([]),
                new AnalysisQueryValidationException([new(AnalysisQueryValidationCode.NameInvalid, "known"),
                    new((AnalysisQueryValidationCode)int.MaxValue, "unknown")]),
                new AnalysisQueryValidationException([new(AnalysisQueryValidationCode.WorkspaceTimeRequired, "missing time parameter")]),
                new InvalidOperationException("Name is required and may contain at most 256 characters.")
            })
                Assert.Equal(text["Analysis.Errors.ExecutionFailed"],
                    AnalysisErrorPresentation.Message(fallback, text, "Analysis.Errors.ExecutionFailed"));
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Theory]
    [InlineData("key", 128, AnalysisQueryValidationCode.KeyInvalid)]
    [InlineData("name", 256, AnalysisQueryValidationCode.NameInvalid)]
    [InlineData("category", 128, AnalysisQueryValidationCode.CategoryInvalid)]
    public void QuerySchemaKeepsExactTrimmedLengthLimits(string field, int limit, AnalysisQueryValidationCode code)
    {
        var query = ActiveDirectoryAnalysisQueries.Create().First();
        void Set(int length)
        {
            var value = "  " + new string('x', length) + "  ";
            switch (field)
            {
                case "key": query.Key = value; break;
                case "name": query.Name = value; break;
                case "category": query.Category = value; break;
                default: throw new ArgumentOutOfRangeException(nameof(field));
            }
        }
        Set(limit);
        AnalysisQueryValidator.RequireValidDefinition(query);
        Set(limit + 1);
        Assert.Equal(code, Assert.Single(Assert.Throws<AnalysisQueryValidationException>(
            () => AnalysisQueryValidator.RequireValidDefinition(query)).Issues).Code);
    }

    [Theory]
    [InlineData("default", 5, 43200, AnalysisQueryValidationCode.DefaultTimeRangeInvalid)]
    [InlineData("max", 5, 43200, AnalysisQueryValidationCode.MaxTimeRangeInvalid)]
    [InlineData("bin", 1, 1440, AnalysisQueryValidationCode.BinInvalid)]
    public void QuerySchemaKeepsTimeAndIntervalBoundaries(string field, int minimum, int maximum, AnalysisQueryValidationCode code)
    {
        var query = ActiveDirectoryAnalysisQueries.Create().First();
        query.DefaultTimeRangeMinutes = 5;
        query.MaxTimeRangeMinutes = 43200;
        void Set(int value)
        {
            switch (field)
            {
                case "default": query.DefaultTimeRangeMinutes = value; break;
                case "max": query.MaxTimeRangeMinutes = value; break;
                case "bin": query.DefaultBinMinutes = value; break;
                default: throw new ArgumentOutOfRangeException(nameof(field));
            }
        }
        foreach (var value in new[] { minimum, maximum })
        {
            Set(value);
            AnalysisQueryValidator.RequireValidDefinition(query);
        }
        foreach (var value in new[] { minimum - 1, maximum + 1 })
        {
            Set(value);
            Assert.Contains(Assert.Throws<AnalysisQueryValidationException>(
                () => AnalysisQueryValidator.RequireValidDefinition(query)).Issues, issue => issue.Code == code);
        }
    }

    public static IEnumerable<object[]> ValidationCases() =>
        Enum.GetValues<AnalysisValidationCode>().SelectMany(code =>
            new[] { "en-US", "de-DE" }.Select(culture => new object[] { code, culture }));

    [Theory]
    [MemberData(nameof(ValidationCases))]
    public void MapsOnlyStableCodesAndRetainsCustomArguments(AnalysisValidationCode code, string culture)
    {
        var previousUi = CultureInfo.CurrentUICulture;
        var previousFormat = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            using var services = new ServiceCollection().AddLogging().AddUiLocalization().BuildServiceProvider();
            var text = services.GetRequiredService<UiText>();
            object[] arguments = code switch
            {
                AnalysisValidationCode.TimeRangeInvalid => [1440],
                AnalysisValidationCode.DomainSystemsMissing or AnalysisValidationCode.WorkspaceSystemsMissing =>
                    ["Custom <workspace & domain>"],
                _ => []
            };
            var exception = new AnalysisValidationException(code, "Technical <provider> details.", arguments);
            var message = AnalysisErrorPresentation.Message(exception, text, "Analysis.Errors.ExecutionFailed");
            Assert.Equal(text.Format($"Analysis.Validation.{code}", arguments), message);
            Assert.DoesNotContain(exception.Message, message);
            Assert.Equal("Technical <provider> details.", exception.Message);
            Assert.IsAssignableFrom<InvalidOperationException>(exception);
            Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
            if (code is AnalysisValidationCode.DomainSystemsMissing or AnalysisValidationCode.WorkspaceSystemsMissing)
            {
                Assert.Contains("Custom <workspace & domain>", message);
                arguments[0] = "Changed caller argument";
                Assert.Equal("Custom <workspace & domain>", exception.Arguments[0]);
            }
            Assert.Equal(text["Analysis.Errors.ExecutionFailed"],
                AnalysisErrorPresentation.Message(new InvalidOperationException("Select between 1 and 100 systems."),
                    text, "Analysis.Errors.ExecutionFailed"));
            Assert.Equal(text["Analysis.Errors.ExecutionFailed"],
                AnalysisErrorPresentation.Message(new AnalysisValidationException((AnalysisValidationCode)999, "Unknown code"),
                    text, "Analysis.Errors.ExecutionFailed"));
            Assert.Equal(text["Analysis.Errors.ExecutionNotAllowed"],
                AnalysisErrorPresentation.Message(new UnauthorizedAccessException("Technical permission details"),
                    text, "Analysis.Errors.ExecutionFailed"));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousUi;
            CultureInfo.CurrentCulture = previousFormat;
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public void MalformedKnownValidationArgumentsUseWholeFallbackWithoutFormattingProviderObjects(string culture)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            using var services = new ServiceCollection().AddLogging().AddUiLocalization().BuildServiceProvider();
            var text = services.GetRequiredService<UiText>();
            foreach (var code in Enum.GetValues<AnalysisValidationCode>())
            {
                var provider = new ThrowingArgument();
                object[][] invalid = code switch
                {
                    AnalysisValidationCode.TimeRangeInvalid =>
                        [[], [null!], ["PRIVATE_ARGUMENT"], [1440L], [1440d], [4], [1440, provider], [provider]],
                    AnalysisValidationCode.DomainSystemsMissing or AnalysisValidationCode.WorkspaceSystemsMissing =>
                        [[], [null!], [123], [""], [" "], ["Custom <name>", provider], [provider]],
                    _ => [[provider], ["PRIVATE_ARGUMENT"]]
                };
                foreach (var arguments in invalid)
                {
                    var exception = new AnalysisValidationException(code, "PRIVATE_DIAGNOSTIC", arguments);
                    Assert.Equal(text["Analysis.Errors.ExecutionFailed"],
                        AnalysisErrorPresentation.Message(exception, text, "Analysis.Errors.ExecutionFailed"));
                    Assert.Equal("PRIVATE_DIAGNOSTIC", exception.Message);
                    Assert.Equal(arguments, exception.Arguments);
                }
                Assert.False(provider.WasFormatted);
            }
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public void QueryIssueParameterShapeIsValidatedBeforeAnyPartOfTheListIsProjected(string culture)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            using var services = new ServiceCollection().AddLogging().AddUiLocalization().BuildServiceProvider();
            var text = services.GetRequiredService<UiText>();
            foreach (var code in Enum.GetValues<AnalysisQueryValidationCode>())
            {
                var malformed = new AnalysisQueryValidationIssue(code, "PRIVATE_ISSUE",
                    code == AnalysisQueryValidationCode.WorkspaceTimeRequired ? " " : "PRIVATE_UNEXPECTED_PARAMETER");
                var exception = new AnalysisQueryValidationException([
                    new(AnalysisQueryValidationCode.KqlRequired, "Original preceding issue"), malformed]);
                Assert.Equal(text["Analysis.Errors.ExecutionFailed"],
                    AnalysisErrorPresentation.Message(exception, text, "Analysis.Errors.ExecutionFailed"));
                Assert.Equal("Original preceding issue PRIVATE_ISSUE", exception.Message);
                Assert.Same(malformed, exception.Issues[1]);
            }
            foreach (var parameter in new[] { "_TimePeriod", "_WindowStart", "_WindowEnd", "Custom <parameter>" })
            {
                var exception = new AnalysisQueryValidationException([
                    new(AnalysisQueryValidationCode.WorkspaceTimeRequired, "Original diagnostic", parameter)]);
                Assert.Equal(text.Format("AnalysisQuery.Validation.WorkspaceTimeRequired", parameter),
                    AnalysisErrorPresentation.Message(exception, text, "Analysis.Errors.ExecutionFailed"));
            }
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    private sealed class ThrowingArgument : IFormattable
    {
        public bool WasFormatted { get; private set; }
        public string ToString(string? format, IFormatProvider? provider)
        {
            WasFormatted = true;
            throw new InvalidOperationException("PRIVATE_FORMATTER_DETAIL");
        }
        public override string ToString() => ToString(null, null);
    }

    [Theory]
    [InlineData(255)]
    [InlineData(256)]
    public void NameLimitAcceptsBoundaryWithoutChangingStoredCustomText(int length)
    {
        var collection = AnalysisDashboardCollection.Create();
        var name = new string('x', length);
        collection.Add("  " + name + "  ");
        Assert.Equal(name, collection.Active.Document.Name);
        collection.RenameActive(name);
        Assert.Equal(name, AnalysisDashboardCollection.Deserialize(collection.Serialize()).Active.Document.Name);
    }
}
