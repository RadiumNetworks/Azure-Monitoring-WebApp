using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MonitoringApp;

public sealed class SqlCommandFailureLoggingInterceptor(
    ILogger<SqlCommandFailureLoggingInterceptor> logger) : DbCommandInterceptor
{
    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) =>
        LogFailure(command.Connection, command.CommandType.ToString(), eventData.Duration, eventData.Exception);

    public override Task CommandFailedAsync(
        DbCommand command,
        CommandErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        LogFailure(command.Connection, command.CommandType.ToString(), eventData.Duration, eventData.Exception);
        return Task.CompletedTask;
    }

    private void LogFailure(
        DbConnection? connection,
        string operation,
        TimeSpan duration,
        Exception exception)
    {
        var sqlException = FindSqlException(exception);
        logger.LogError(
            exception,
            "SQL command failed. Server={Server}, Database={Database}, Operation={Operation}, DurationMs={DurationMs}, SqlErrorNumber={SqlErrorNumber}, SqlState={SqlState}, SqlClass={SqlClass}, ClientConnectionId={ClientConnectionId}",
            connection?.DataSource ?? "(unknown)",
            connection?.Database ?? "(unknown)",
            operation,
            duration.TotalMilliseconds,
            sqlException?.Number,
            sqlException?.State,
            sqlException?.Class,
            sqlException?.ClientConnectionId);
    }

    private static SqlException? FindSqlException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is SqlException sqlException)
            {
                return sqlException;
            }
        }

        return null;
    }
}

public sealed class SqlConnectionFailureLoggingInterceptor(
    ILogger<SqlConnectionFailureLoggingInterceptor> logger) : DbConnectionInterceptor
{
    public override void ConnectionFailed(DbConnection connection, ConnectionErrorEventData eventData) =>
        LogFailure(connection, eventData.Exception);

    public override Task ConnectionFailedAsync(
        DbConnection connection,
        ConnectionErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        LogFailure(connection, eventData.Exception);
        return Task.CompletedTask;
    }

    private void LogFailure(DbConnection connection, Exception exception)
    {
        var sqlException = exception as SqlException ?? exception.InnerException as SqlException;
        logger.LogError(
            exception,
            "SQL connection failed. Server={Server}, Database={Database}, SqlErrorNumber={SqlErrorNumber}, SqlState={SqlState}, SqlClass={SqlClass}, ClientConnectionId={ClientConnectionId}",
            connection.DataSource,
            connection.Database,
            sqlException?.Number,
            sqlException?.State,
            sqlException?.Class,
            sqlException?.ClientConnectionId);
    }
}