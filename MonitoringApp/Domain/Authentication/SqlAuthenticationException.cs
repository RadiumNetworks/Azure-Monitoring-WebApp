namespace MonitoringApp;

public enum SqlAuthenticationErrorCode
{
    Username, Password, Role, DuplicateUser, Missing, LastAdmin, LastUserOrAdmin,
    CreateUnconfirmed, PasswordUnconfirmed, DeleteUnconfirmed
}

public sealed class SqlAuthenticationException(
    SqlAuthenticationErrorCode code, string message, Exception? innerException = null, params object[] arguments)
    : InvalidOperationException(message, innerException)
{
    public SqlAuthenticationErrorCode Code { get; } = code;
    public IReadOnlyList<object> Arguments { get; } = Array.AsReadOnly(arguments.ToArray());
}
