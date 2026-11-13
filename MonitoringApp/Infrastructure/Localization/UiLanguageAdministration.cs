using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace MonitoringApp;

public enum UiLanguageAdminError { AccessDenied, Configuration, Conflict, Storage, Unhealthy, TooLarge, ReviewRequired, NoChanges, NoPrevious, Unconfirmed }
public enum UiLanguagePackIssueCode { Json, DuplicateKey, UnknownKey, ValueType, TextLength, EmptyEnglish, Parameters, MissingEnglish }
public sealed record UiLanguagePackIssue(UiLanguagePackIssueCode Code, string Key);
public sealed record UiLanguagePackReport(string Culture, string File, IReadOnlyList<string> MissingKeys, IReadOnlyList<UiLanguagePackIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}
public sealed record UiLanguageAdminState(string Revision, string DefaultCulture, IReadOnlyList<UiLanguageDefinition> Languages,
    string RegistryJson, bool CanRollback);
public sealed record UiLanguageDownload(string File, byte[] Bytes);

public sealed class UiLanguageAdminException(UiLanguageAdminError code, Exception? inner = null)
    : Exception($"UI language administration failed: {code}.", inner)
{
    public UiLanguageAdminError Code { get; } = code;
}

public sealed class UiLanguageUpload
{
    internal UiLanguageUpload(byte[] bytes)
    {
        Bytes = bytes.ToArray();
        File = $"ui-upload-{Guid.NewGuid():N}.json";
    }
    public string File { get; }
    internal byte[] Bytes { get; }
}

public sealed class UiLanguagePreview
{
    internal UiLanguagePreview(UiLanguageConfiguration configuration, IReadOnlyList<UiLanguageUpload> uploads,
        byte[] registry, string expected, string revision, IReadOnlyList<UiLanguagePackReport> reports, bool rollback)
    {
        Configuration = configuration; Uploads = uploads; RegistryBytes = registry;
        ExpectedRevision = expected; Revision = revision; Reports = reports; IsRollback = rollback;
    }
    internal UiLanguageConfiguration Configuration { get; }
    internal IReadOnlyList<UiLanguageUpload> Uploads { get; }
    internal byte[] RegistryBytes { get; }
    public string ExpectedRevision { get; }
    public string Revision { get; }
    public IReadOnlyList<UiLanguagePackReport> Reports { get; }
    public bool IsRollback { get; }
    public bool CanActivate => Reports.All(report => report.IsValid) && Revision != ExpectedRevision;
}

public sealed class UiLanguageAdministration(
    JsonUiLanguageStore store, IAuthorizationService authorization,
    ApplicationAuthenticationOptions authentication, ILogger<UiLanguageAdministration> logger)
{
    public Task<UiLanguageAdminState> LoadAsync(ClaimsPrincipal user, CancellationToken token = default) =>
        Execute(user, store.AdminLoad, token);

    public Task<UiLanguageUpload> StageUploadAsync(ClaimsPrincipal user, byte[] bytes, CancellationToken token = default) =>
        Execute(user, () => bytes.Length <= JsonUiLanguageStore.MaximumFileBytes
            ? new UiLanguageUpload(bytes) : throw new UiLanguageAdminException(UiLanguageAdminError.TooLarge), token);

    public Task<UiLanguagePreview> PreviewAsync(ClaimsPrincipal user, string expectedRevision, string defaultCulture,
        IReadOnlyList<UiLanguageDefinition> languages, IReadOnlyList<UiLanguageUpload> uploads, CancellationToken token = default) =>
        Execute(user, () => store.AdminPreview(expectedRevision, defaultCulture, languages, uploads), token);

    public Task<UiLanguagePreview> PreviewRollbackAsync(ClaimsPrincipal user, string expectedRevision, CancellationToken token = default) =>
        Execute(user, () => store.AdminRollback(expectedRevision), token);

    public Task<UiLanguageAdminState> ActivateAsync(ClaimsPrincipal user, UiLanguagePreview preview, CancellationToken token = default) =>
        Execute(user, () =>
        {
            var state = store.AdminActivate(preview);
            logger.LogInformation("UI language configuration activated. Revision={Revision}, Rollback={Rollback}, OpenMode={OpenMode}.",
                state.Revision, preview.IsRollback, authentication.IsOpen);
            return state;
        }, token);

    public Task<UiLanguageDownload> DownloadAsync(ClaimsPrincipal user, string expectedRevision, string culture, CancellationToken token = default) =>
        Execute(user, () => store.AdminDownload(expectedRevision, culture), token);

    private async Task<T> Execute<T>(ClaimsPrincipal user, Func<T> operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!(await authorization.AuthorizeAsync(user, ApplicationAuthorization.AdminPolicy)).Succeeded)
        {
            logger.LogWarning("A UI language administration operation was denied.");
            throw new UiLanguageAdminException(UiLanguageAdminError.AccessDenied);
        }
        token.ThrowIfCancellationRequested();
        try { return operation(); }
        catch (UiLanguageAdminException exception)
        {
            logger.LogWarning(exception, "UI language administration rejected the operation: {Code}.", exception.Code);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.LogError(exception, "UI language administration could not access its controlled language directory.");
            throw new UiLanguageAdminException(UiLanguageAdminError.Storage, exception);
        }
    }
}
