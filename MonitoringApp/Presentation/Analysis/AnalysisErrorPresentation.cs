namespace MonitoringApp;

internal static class AnalysisErrorPresentation
{
    public static string Message(Exception exception, UiText text, string fallbackKey)
    {
        if (exception is AnalysisQueryPublicationException publication && Enum.IsDefined(publication.Code))
            return text[$"Analysis.Publication.{publication.Code}"];
        if (exception is AnalysisQuerySnapshotJsonException)
            return text["Analysis.Publication.SnapshotInvalid"];
        if (exception is QueryTemplateValidationException templateValidation && Enum.IsDefined(templateValidation.Code))
            return text[$"QueryTemplate.Validation.{templateValidation.Code}"];
        if (exception is AnalysisQueryValidationException queryValidation &&
            queryValidation.Issues.Count > 0 && queryValidation.Issues.All(HasValidParameter))
            return string.Join(" ", queryValidation.Issues.Select(issue => issue.Parameter is null
                ? text[$"AnalysisQuery.Validation.{issue.Code}"]
                : text.Format($"AnalysisQuery.Validation.{issue.Code}", issue.Parameter)));
        if (exception is AnalysisValidationException validation && HasValidArguments(validation))
            return text.Format($"Analysis.Validation.{validation.Code}", validation.Arguments.ToArray());
        return text[exception is UnauthorizedAccessException ? "Analysis.Errors.ExecutionNotAllowed" : fallbackKey];
    }

    private static bool HasValidParameter(AnalysisQueryValidationIssue issue) =>
        Enum.IsDefined(issue.Code) && (issue.Code == AnalysisQueryValidationCode.WorkspaceTimeRequired
            ? !string.IsNullOrWhiteSpace(issue.Parameter)
            : issue.Parameter is null);

    private static bool HasValidArguments(AnalysisValidationException exception) =>
        Enum.IsDefined(exception.Code) && (exception.Code switch
        {
            AnalysisValidationCode.TimeRangeInvalid =>
                exception.Arguments.Count == 1 && exception.Arguments[0] is int maximum && maximum >= 5,
            AnalysisValidationCode.DomainSystemsMissing or AnalysisValidationCode.WorkspaceSystemsMissing =>
                exception.Arguments.Count == 1 && exception.Arguments[0] is string name && !string.IsNullOrWhiteSpace(name),
            _ => exception.Arguments.Count == 0
        });
}
