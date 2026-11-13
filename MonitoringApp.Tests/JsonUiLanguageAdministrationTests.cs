using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoringApp.Components;

namespace MonitoringApp.Tests;

public sealed partial class JsonUiLocalizationTests
{
    private static ClaimsPrincipal LanguageActor(string? role) => new(new ClaimsIdentity(
        role is null ? [] : [new Claim(ClaimTypes.Role, role)], role is null ? null : "test"));

    private static ServiceProvider LanguageAdminServices(LanguageFiles files, bool open = false, JsonUiLanguageStore? store = null)
    {
        var authentication = new ApplicationAuthenticationOptions { Type = open ? "open" : "sql" };
        return new ServiceCollection().AddLogging().AddUiLocalization()
            .AddSingleton(store ?? files.Store).AddSingleton(authentication)
            .AddAuthorization(options => ApplicationAuthorization.Configure(options, authentication))
            .BuildServiceProvider();
    }

    [Theory]
    [InlineData("Reader")]
    [InlineData("Operator")]
    [InlineData(null)]
    public async Task AdminLanguageOperationsAllRequireCurrentAdminAuthorization(string? role)
    {
        using var files = new LanguageFiles();
        using var services = LanguageAdminServices(files);
        var admin = services.GetRequiredService<UiLanguageAdministration>();
        var actor = LanguageActor("Admin");
        var state = await admin.LoadAsync(actor);
        var preview = await admin.PreviewAsync(actor, state.Revision, "de-DE", state.Languages, []);
        var rejected = LanguageActor(role);
        Func<Task>[] operations =
        [
            async () => { await admin.LoadAsync(rejected); },
            async () => { await admin.StageUploadAsync(rejected, []); },
            async () => { await admin.PreviewAsync(rejected, state.Revision, "de-DE", state.Languages, []); },
            async () => { await admin.PreviewRollbackAsync(rejected, state.Revision); },
            async () => { await admin.DownloadAsync(rejected, state.Revision, "en-US"); },
            async () => { await admin.ActivateAsync(rejected, preview); }
        ];
        foreach (var operation in operations)
            Assert.Equal(UiLanguageAdminError.AccessDenied, (await Assert.ThrowsAsync<UiLanguageAdminException>(operation)).Code);
        Assert.Equal(state.Revision, (await admin.LoadAsync(actor)).Revision);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(RegistryPath(files))!, ".languages.lock")));
    }

    [Fact]
    public async Task AdminLanguageOpenModePreservesExistingOpenPolicy()
    {
        using var files = new LanguageFiles();
        using var services = LanguageAdminServices(files, open: true);
        var state = await services.GetRequiredService<UiLanguageAdministration>().LoadAsync(LanguageActor(null));
        Assert.Equal("en-US", state.DefaultCulture);
    }

    [Fact]
    public async Task AdminLanguageUploadPreviewActivateDownloadAndPersistentRollbackPreserveOriginalFiles()
    {
        using var culture = new CultureScope("fr-FR");
        using var files = new LanguageFiles();
        using var services = LanguageAdminServices(files);
        var admin = services.GetRequiredService<UiLanguageAdministration>();
        var actor = LanguageActor("Admin");
        var before = await admin.LoadAsync(actor);
        var english = File.ReadAllBytes(files.PathFor("en-US"));
        var german = File.ReadAllBytes(files.PathFor("de-DE"));
        var directory = Path.GetDirectoryName(RegistryPath(files))!;
        var initialFiles = Directory.GetFiles(directory).Order().ToArray();
        using var oldScope = services.CreateScope();
        var oldText = oldScope.ServiceProvider.GetRequiredService<UiText>();
        var source = Encoding.UTF8.GetBytes("""{"Language.Title":"Langue"}""");
        var staged = await admin.StageUploadAsync(actor, source);
        source[0] = (byte)'[';
        var french = new UiLanguageDefinition { Culture = "fr-FR", DisplayName = "Francais", File = staged.File, Enabled = true };
        var preview = await admin.PreviewAsync(actor, before.Revision, "fr-FR", before.Languages.Append(french).ToArray(), [staged]);
        Assert.True(preview.CanActivate);
        Assert.NotEmpty(preview.Reports.Single(report => report.Culture == "fr-FR").MissingKeys);
        Assert.Equal(initialFiles, Directory.GetFiles(directory).Order());
        var active = await admin.ActivateAsync(actor, preview);
        Assert.Equal("fr-FR", active.DefaultCulture);
        Assert.True(active.CanRollback);
        Assert.Equal(preview.Revision, active.Revision);
        Assert.Equal(english, File.ReadAllBytes(files.PathFor("en-US")));
        Assert.Equal(german, File.ReadAllBytes(files.PathFor("de-DE")));
        Assert.Equal("Language", oldText["Language.Title"]);
        using var newScope = services.CreateScope();
        Assert.Equal("Langue", newScope.ServiceProvider.GetRequiredService<UiText>()["Language.Title"]);
        Assert.Equal(Encoding.UTF8.GetBytes("""{"Language.Title":"Langue"}"""),
            (await admin.DownloadAsync(actor, active.Revision, "fr-FR")).Bytes);

        var cold = new JsonUiLanguageStore(directory, files.Logger);
        using var restartedServices = LanguageAdminServices(files, store: cold);
        var restarted = restartedServices.GetRequiredService<UiLanguageAdministration>();
        Assert.True((await restarted.LoadAsync(actor)).CanRollback);
        var previous = await restarted.PreviewRollbackAsync(actor, active.Revision);
        Assert.True(previous.IsRollback);
        Assert.True(previous.CanActivate);
        var restored = await restarted.ActivateAsync(actor, previous);
        Assert.Equal("en-US", restored.DefaultCulture);
        Assert.Equal(new[] { "en-US", "de-DE" }, restored.Languages.Select(language => language.Culture));
        Assert.Equal(english, (await restarted.DownloadAsync(actor, restored.Revision, "en-US")).Bytes);
        var redo = await restarted.PreviewRollbackAsync(actor, restored.Revision);
        Assert.Equal("fr-FR", (await restarted.ActivateAsync(actor, redo)).DefaultCulture);
    }

    [Fact]
    public async Task AdminLanguagePreviewCollectsUnknownDuplicateTypeAndParameterErrorsWithoutWriting()
    {
        using var files = new LanguageFiles();
        using var services = LanguageAdminServices(files);
        var admin = services.GetRequiredService<UiLanguageAdministration>();
        var actor = LanguageActor("Admin");
        var state = await admin.LoadAsync(actor);
        var upload = await admin.StageUploadAsync(actor,
            Encoding.UTF8.GetBytes("""{"Language.Title":"{0}","Unknown.Key":"x","Language.Apply":9,"Language.Apply":"duplicate"}"""));
        var entries = state.Languages.Select(language => language.Culture == "de-DE" ? language with { File = upload.File } : language).ToArray();
        var prepared = await admin.PreviewAsync(actor, state.Revision, "en-US", entries, [upload]);
        Assert.False(prepared.CanActivate);
        var report = prepared.Reports.Single(item => item.Culture == "de-DE");
        Assert.Contains(report.Issues, issue => issue.Code == UiLanguagePackIssueCode.UnknownKey);
        Assert.Contains(report.Issues, issue => issue.Code == UiLanguagePackIssueCode.DuplicateKey);
        Assert.Contains(report.Issues, issue => issue.Code == UiLanguagePackIssueCode.ValueType);
        Assert.Contains(report.Issues, issue => issue.Code == UiLanguagePackIssueCode.Parameters);
        Assert.Equal(UiLanguageAdminError.ReviewRequired,
            (await Assert.ThrowsAsync<UiLanguageAdminException>(() => admin.ActivateAsync(actor, prepared))).Code);
        Assert.Equal(state.Revision, (await admin.LoadAsync(actor)).Revision);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(RegistryPath(files))!, upload.File)));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"Language.Title":" "}""")]
    [InlineData("[]")]
    [InlineData("not json")]
    public async Task AdminLanguageInvalidEnglishAndMalformedUploadsCannotActivate(string json)
    {
        using var files = new LanguageFiles();
        using var services = LanguageAdminServices(files);
        var service = services.GetRequiredService<UiLanguageAdministration>();
        var actor = LanguageActor("Admin");
        var state = await service.LoadAsync(actor);
        var upload = await service.StageUploadAsync(actor, Encoding.UTF8.GetBytes(json));
        var entries = state.Languages.Select(language => language.Culture == "en-US" ? language with { File = upload.File } : language).ToArray();
        var preview = await service.PreviewAsync(actor, state.Revision, "en-US", entries, [upload]);
        Assert.False(preview.CanActivate);
        Assert.Contains(preview.Reports.Single(report => report.Culture == "en-US").Issues,
            issue => issue.Code == UiLanguagePackIssueCode.MissingEnglish);
    }

    [Fact]
    public async Task AdminLanguageUploadLimitAndConfigurationValidationAreExplicit()
    {
        using var files = new LanguageFiles();
        using var services = LanguageAdminServices(files);
        var service = services.GetRequiredService<UiLanguageAdministration>();
        var actor = LanguageActor("Admin");
        var state = await service.LoadAsync(actor);
        Assert.Equal(UiLanguageAdminError.TooLarge,
            (await Assert.ThrowsAsync<UiLanguageAdminException>(() => service.StageUploadAsync(actor,
                new byte[JsonUiLanguageStore.MaximumFileBytes + 1]))).Code);
        Assert.Equal(JsonUiLanguageStore.MaximumFileBytes,
            (await service.StageUploadAsync(actor, new byte[JsonUiLanguageStore.MaximumFileBytes])).Bytes.Length);
        var bad = state.Languages.Select(language => language.Culture == "de-DE" ? language with { File = "..\\outside.json" } : language).ToArray();
        Assert.Equal(UiLanguageAdminError.Configuration,
            (await Assert.ThrowsAsync<UiLanguageAdminException>(() => service.PreviewAsync(actor, state.Revision, "en-US", bad, []))).Code);
        Assert.Equal(UiLanguageAdminError.Configuration,
            (await Assert.ThrowsAsync<UiLanguageAdminException>(() => service.PreviewAsync(actor, state.Revision, "fr-FR", state.Languages, []))).Code);
    }

    [Fact]
    public async Task AdminLanguageStaleRevisionAndChangedCandidateBothBlockActivation()
    {
        using var files = new LanguageFiles();
        using var services = LanguageAdminServices(files);
        var service = services.GetRequiredService<UiLanguageAdministration>();
        var actor = LanguageActor("Admin");
        var state = await service.LoadAsync(actor);
        var preview = await service.PreviewAsync(actor, state.Revision, "de-DE", state.Languages, []);
        files.Edit("de-DE", values => values["Language.Title"] = "Externally changed");
        Assert.Equal(UiLanguageAdminError.Conflict,
            (await Assert.ThrowsAsync<UiLanguageAdminException>(() => service.ActivateAsync(actor, preview))).Code);
        var refreshed = await service.LoadAsync(actor);
        var candidate = Path.Combine(Path.GetDirectoryName(RegistryPath(files))!, "candidate.json");
        File.Copy(files.PathFor("de-DE"), candidate);
        var entries = refreshed.Languages.Select(language => language.Culture == "de-DE" ? language with { File = "candidate.json" } : language).ToArray();
        var check = await service.PreviewAsync(actor, refreshed.Revision, "de-DE", entries, []);
        File.WriteAllText(candidate, """{"Language.Title":"Another candidate"}""");
        Assert.Equal(UiLanguageAdminError.Conflict,
            (await Assert.ThrowsAsync<UiLanguageAdminException>(() => service.ActivateAsync(actor, check))).Code);
        Assert.Equal(refreshed.Revision, (await service.LoadAsync(actor)).Revision);
    }

    [Fact]
    public async Task AdminLanguageSeparateInstancesAndHeldWriteLockCannotOverwriteEachOther()
    {
        using var files = new LanguageFiles();
        using var first = LanguageAdminServices(files);
        var directory = Path.GetDirectoryName(RegistryPath(files))!;
        using var second = LanguageAdminServices(files, store: new JsonUiLanguageStore(directory, files.Logger));
        var a = first.GetRequiredService<UiLanguageAdministration>();
        var b = second.GetRequiredService<UiLanguageAdministration>();
        var actor = LanguageActor("Admin");
        var state = await a.LoadAsync(actor);
        var pa = await a.PreviewAsync(actor, state.Revision, "de-DE", state.Languages, []);
        var pb = await b.PreviewAsync(actor, state.Revision, "de-DE", state.Languages, []);
        using (var held = new FileStream(Path.Combine(directory, ".languages.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(UiLanguageAdminError.Conflict,
                (await Assert.ThrowsAsync<UiLanguageAdminException>(() => a.ActivateAsync(actor, pa))).Code);
        var active = await a.ActivateAsync(actor, pa);
        Assert.Equal(UiLanguageAdminError.Conflict,
            (await Assert.ThrowsAsync<UiLanguageAdminException>(() => b.ActivateAsync(actor, pb))).Code);
        Assert.Equal(active.Revision, (await b.LoadAsync(actor)).Revision);
    }

    [Fact]
    public async Task AdminLanguageWriteFailureLeavesLiveManifestAndRuntimeUntouched()
    {
        using var files = new LanguageFiles();
        using var services = LanguageAdminServices(files);
        var service = services.GetRequiredService<UiLanguageAdministration>();
        var actor = LanguageActor("Admin");
        var state = await service.LoadAsync(actor);
        var preview = await service.PreviewAsync(actor, state.Revision, "de-DE", state.Languages, []);
        var bytes = File.ReadAllBytes(RegistryPath(files));
        using (var held = new FileStream(RegistryPath(files), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Equal(UiLanguageAdminError.Storage,
                (await Assert.ThrowsAsync<UiLanguageAdminException>(() => service.ActivateAsync(actor, preview))).Code);
        Assert.Equal(bytes, File.ReadAllBytes(RegistryPath(files)));
        Assert.Equal(state.Revision, (await service.LoadAsync(actor)).Revision);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(RegistryPath(files))!, "*.tmp"));
    }

    [Fact]
    public async Task AdminLanguageUnhealthyFilesAreNotSilentlyOverwrittenAndNoPreviousIsExplicit()
    {
        using var files = new LanguageFiles();
        using var services = LanguageAdminServices(files);
        var service = services.GetRequiredService<UiLanguageAdministration>();
        var actor = LanguageActor("Admin");
        var state = await service.LoadAsync(actor);
        Assert.Equal(UiLanguageAdminError.NoPrevious,
            (await Assert.ThrowsAsync<UiLanguageAdminException>(() => service.PreviewRollbackAsync(actor, state.Revision))).Code);
        files.Write("de-DE", "{");
        Assert.Equal(UiLanguageAdminError.Unhealthy,
            (await Assert.ThrowsAsync<UiLanguageAdminException>(() => service.LoadAsync(actor))).Code);
    }

    [Fact]
    public async Task AdminLanguageUnchangedDraftCannotCreateAnUnnecessaryActivation()
    {
        using var files = new LanguageFiles();
        using var services = LanguageAdminServices(files);
        var service = services.GetRequiredService<UiLanguageAdministration>();
        var actor = LanguageActor("Admin");
        var state = await service.LoadAsync(actor);
        var preview = await service.PreviewAsync(actor, state.Revision, state.DefaultCulture, state.Languages, []);
        Assert.False(preview.CanActivate);
        Assert.Equal(state.Revision, preview.Revision);
        Assert.Equal(UiLanguageAdminError.ReviewRequired,
            (await Assert.ThrowsAsync<UiLanguageAdminException>(() => service.ActivateAsync(actor, preview))).Code);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task AdminLanguagePanelStagesUploadReviewsActivatesAndInvalidatesEdits(string culture)
    {
        using var selected = new CultureScope(culture);
        using var files = new LanguageFiles();
        await using var ui = await LanguageUi.Create(files);
        Assert.Contains(ui.Text["LanguageAdmin.Title"], ui.Html);
        Assert.Contains("data-language-administration", ui.Html);
        Assert.DoesNotContain("settings-form", ui.Html);
        var rowList = (System.Collections.IList)ui.Field("rows")!;
        var german = rowList[1]!;
        await ui.Call("UploadAsync", german, new InputFileChangeEventArgs([new LanguageBrowserFile("""{"Language.Title":"Live Sprache"}""")]));
        Assert.DoesNotContain("data-language-preview", ui.Html);
        await ui.Call("PreviewAsync");
        Assert.Contains("data-language-preview", ui.Html);
        Assert.Contains(ui.Text["LanguageAdmin.DownloadReport"], ui.Html);
        await ui.Call("ActivateAsync");
        Assert.Contains(ui.Text["LanguageAdmin.Errors.ReviewRequired"], ui.Html);
        ui.Set("reviewed", true);
        await ui.Call("Changed");
        Assert.Null(ui.Field("preview"));
        Assert.False(ui.Get<bool>("reviewed"));
        await ui.Call("PreviewAsync");
        ui.Set("reviewed", true);
        await ui.Call("ActivateAsync");
        Assert.Contains(ui.Text["LanguageAdmin.Activated"], ui.Html);
        Assert.Equal("Live Sprache", files.Store.Read().Packs["de-DE"]["Language.Title"]);
        Assert.Null(ui.Field("preview"));
        Assert.True(ui.Get<UiLanguageAdminState>("state").CanRollback);
    }

    [Fact]
    public async Task AdminLanguagePanelDiscardsUploadsWhenTheirMappingOrRowIsRemoved()
    {
        using var files = new LanguageFiles();
        await using var ui = await LanguageUi.Create(files);
        var rows = ui.Get<System.Collections.IList>("rows");
        var german = rows[1]!;
        await ui.Call("UploadAsync", german, new InputFileChangeEventArgs([new LanguageBrowserFile("{}")]));
        Assert.Single(ui.Get<System.Collections.IList>("uploads").Cast<object>());
        german.GetType().GetProperty("File")!.SetValue(german, "ui.de-DE.json");
        await ui.Call("Changed");
        Assert.Empty(ui.Get<System.Collections.IList>("uploads"));
        await ui.Call("UploadAsync", german, new InputFileChangeEventArgs([new LanguageBrowserFile("{}")]));
        await ui.Call("Remove", german);
        Assert.Empty(ui.Get<System.Collections.IList>("uploads"));
    }

    [Fact]
    public async Task AdminLanguagePanelEnforcesTheThirtyTwoLanguageLimitInItsHandler()
    {
        using var files = new LanguageFiles();
        await using var ui = await LanguageUi.Create(files);
        var rows = ui.Get<System.Collections.IList>("rows");
        while (rows.Count < 32) await ui.Call("Add");
        await ui.Call("Add");
        Assert.Equal(32, rows.Count);
        Assert.Contains(ui.Text["LanguageAdmin.Errors.Configuration"], ui.Html);
    }

    [Fact]
    public async Task AdminLanguagePanelRollbackPreviewShowsRestoredNamesAndSelectionState()
    {
        using var files = new LanguageFiles();
        await using var ui = await LanguageUi.Create(files);
        var originalEnglish = ui.Get<UiLanguageAdminState>("state").Languages[0];
        var english = ui.Get<System.Collections.IList>("rows")[0]!;
        english.GetType().GetProperty("DisplayName")!.SetValue(english, "Changed English label");
        english.GetType().GetProperty("Enabled")!.SetValue(english, false);
        ui.Set("selectedDefault", "de-DE");
        await ui.Call("PreviewAsync");
        ui.Set("reviewed", true);
        await ui.Call("ActivateAsync");
        await ui.Call("PreviewRollbackAsync");
        var html = ui.Html;
        var previewHtml = html[html.IndexOf("data-language-preview", StringComparison.Ordinal)..];
        Assert.DoesNotContain("Changed English label", previewHtml);
        Assert.Matches(
            System.Text.RegularExpressions.Regex.Escape(originalEnglish.DisplayName) +
            @" \(<code>en-US</code>\): <code>[^<]+</code> - " +
            System.Text.RegularExpressions.Regex.Escape(ui.Text["Common.Enabled"]), previewHtml);
    }

    [Fact]
    public async Task AdminLanguagePanelRechecksRevokedRightsBeforeActivationAndClearsLoadedData()
    {
        using var files = new LanguageFiles();
        await using var ui = await LanguageUi.Create(files);
        var before = files.Store.Read().Revision;
        ui.Set("selectedDefault", "de-DE");
        await ui.Call("PreviewAsync");
        ui.Set("reviewed", true);
        ui.Authentication.User = LanguageActor("Reader");
        await ui.Call("ActivateAsync");
        Assert.Null(ui.Field("state"));
        Assert.Null(ui.Field("preview"));
        Assert.Contains(ui.Text["LanguageAdmin.Errors.AccessDenied"], ui.Html);
        Assert.DoesNotContain("data-language-preview", ui.Html);
        Assert.Equal(before, files.Store.Read().Revision);
    }

    [Fact]
    public async Task AdminLanguagePanelConflictRequiresExplicitReloadRatherThanRetrying()
    {
        using var files = new LanguageFiles();
        await using var ui = await LanguageUi.Create(files);
        ui.Set("selectedDefault", "de-DE");
        await ui.Call("PreviewAsync");
        ui.Set("reviewed", true);
        files.Edit("de-DE", values => values["Language.Title"] = "Changed by another administrator");
        await ui.Call("ActivateAsync");
        Assert.True(ui.Get<bool>("blocked"));
        Assert.Null(ui.Field("preview"));
        Assert.Contains(ui.Text["LanguageAdmin.Errors.Conflict"], ui.Html);
        Assert.Equal("en-US", files.Store.Read().Configuration.DefaultCulture);
        await ui.Call("LoadAsync");
        Assert.False(ui.Get<bool>("blocked"));
    }

    [Fact]
    public async Task AdminLanguagePanelShowsExplicitOpenModeWarning()
    {
        using var files = new LanguageFiles();
        await using var ui = await LanguageUi.Create(files, open: true);
        Assert.Contains(ui.Text["LanguageAdmin.OpenWarning"], ui.Html);
    }

    private sealed class LanguageBrowserFile(string json) : IBrowserFile
    {
        private readonly byte[] bytes = Encoding.UTF8.GetBytes(json);
        public string Name => "..\\ignored-client-name.json";
        public DateTimeOffset LastModified => DateTimeOffset.UtcNow;
        public long Size => bytes.Length;
        public string ContentType => "application/json";
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) =>
            bytes.Length <= maxAllowedSize ? new MemoryStream(bytes, writable: false) : throw new IOException("size");
    }

    private sealed class LanguageAuthentication : AuthenticationStateProvider
    {
        public ClaimsPrincipal User { get; set; } = LanguageActor("Admin");
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(User));
    }

    private sealed class LanguageActivator : IComponentActivator
    {
        public UiLanguageSettings? Component { get; private set; }
        public IComponent CreateInstance(Type type)
        {
            var component = (IComponent)Activator.CreateInstance(type)!;
            if (component is UiLanguageSettings settings) Component = settings;
            return component;
        }
    }

    private sealed class LanguageUi(WebApplication app, AsyncServiceScope scope, HtmlRenderer renderer,
        Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent root, UiLanguageSettings component) : IAsyncDisposable
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        public UiText Text => scope.ServiceProvider.GetRequiredService<UiText>();
        public LanguageAuthentication Authentication => (LanguageAuthentication)scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>();
        public string Html => renderer.Dispatcher.InvokeAsync(() => System.Net.WebUtility.HtmlDecode(root.ToHtmlString())).GetAwaiter().GetResult();
        public object? Field(string name) => typeof(UiLanguageSettings).GetField(name, Flags)!.GetValue(component);
        public T Get<T>(string name) => (T)Field(name)!;
        public void Set(string name, object value) => typeof(UiLanguageSettings).GetField(name, Flags)!.SetValue(component, value);
        public async Task Call(string name, params object[] arguments) => await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = typeof(UiLanguageSettings).GetMethod(name, Flags)!.Invoke(component, arguments);
            if (result is Task task) await task;
            typeof(ComponentBase).GetMethod("StateHasChanged", Flags)!.Invoke(component, null);
        });
        public static async Task<LanguageUi> Create(LanguageFiles files, bool open = false)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddRazorComponents().AddInteractiveServerComponents();
            var authentication = new ApplicationAuthenticationOptions { Type = open ? "open" : "sql" };
            builder.Services.AddSingleton(authentication);
            builder.Services.AddAuthorization(options => ApplicationAuthorization.Configure(options, authentication));
            builder.Services.AddScoped<AuthenticationStateProvider, LanguageAuthentication>();
            builder.Services.AddScoped<NavigationManager, TestNavigation>();
            builder.Services.AddUiLocalization();
            builder.Services.AddSingleton(files.Store);
            builder.Services.AddSingleton<LanguageActivator>();
            builder.Services.AddSingleton<IComponentActivator>(services => services.GetRequiredService<LanguageActivator>());
            var app = builder.Build();
            var scope = app.Services.CreateAsyncScope();
            var renderer = new HtmlRenderer(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>());
            var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<UiLanguageSettings>());
            return new(app, scope, renderer, root, scope.ServiceProvider.GetRequiredService<LanguageActivator>().Component!);
        }
        public async ValueTask DisposeAsync()
        {
            await renderer.DisposeAsync();
            await scope.DisposeAsync();
            await app.DisposeAsync();
        }
    }
}
