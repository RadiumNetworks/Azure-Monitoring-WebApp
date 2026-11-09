namespace MonitoringApp;

public static class AgentAnalysisErrorPresentation
{
    public static string DisabledReason(AgentAnalysisExecutionState state, UiText text) =>
        state.DisabledCode is { } code && Enum.IsDefined(code)
            ? text[$"AgentAnalysis.Disabled.{code}"]
            : text["AgentAnalysis.Disabled.Unknown"];
}
