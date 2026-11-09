namespace MonitoringApp;

public static class DiagnosticErrorPresentation
{
    public static string Name(DiagnosticStep step, UiText text) => step.NameCode switch
    {
        null => step.Name,
        DiagnosticNameCode.KeyVault => text["Diagnostics.Name.KeyVault"],
        DiagnosticNameCode.TenantToken => text["Diagnostics.Name.TenantToken"],
        DiagnosticNameCode.WorkspaceQuery => text["Diagnostics.Name.WorkspaceQuery"],
        _ => text["Diagnostics.Name.Unknown"]
    };

    public static string Message(DiagnosticStep step, UiText text)
    {
        if (step.MessageCode is null) return step.Message;
        var expected = step.MessageCode switch
        {
            DiagnosticMessageCode.Succeeded => DiagnosticStatus.Succeeded,
            DiagnosticMessageCode.Disabled => DiagnosticStatus.Disabled,
            DiagnosticMessageCode.KeyVaultRequired or DiagnosticMessageCode.TokenRequired => DiagnosticStatus.NotRun,
            DiagnosticMessageCode.KeyVaultAuthenticationFailed or DiagnosticMessageCode.KeyVaultRequestFailed or
            DiagnosticMessageCode.KeyVaultTimeout or DiagnosticMessageCode.KeyVaultFailed or
            DiagnosticMessageCode.TokenAuthenticationFailed or DiagnosticMessageCode.TokenTimeout or
            DiagnosticMessageCode.QueryUnexpected or DiagnosticMessageCode.QueryRequestFailed or
            DiagnosticMessageCode.QueryTimeout => DiagnosticStatus.Failed,
            _ => (DiagnosticStatus?)null
        };
        if (expected != step.Status) return text["Diagnostics.Message.Unavailable"];
        var key = $"Diagnostics.Message.{step.MessageCode}";
        if (step.MessageCode is DiagnosticMessageCode.KeyVaultRequestFailed or DiagnosticMessageCode.QueryRequestFailed)
            return step.HttpStatus is { } status
                ? text.Format(key, status)
                : text["Diagnostics.Message.Unavailable"];
        return text[key];
    }
}
