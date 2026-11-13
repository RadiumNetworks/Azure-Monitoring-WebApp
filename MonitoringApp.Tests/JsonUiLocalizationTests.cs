using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using MonitoringApp.Components;

namespace MonitoringApp.Tests;

public sealed partial class JsonUiLocalizationTests
{
    [Theory]
    [InlineData("en-US", "UiResources.resx")]
    [InlineData("de-DE", "UiResources.de-DE.resx")]
    public void BundledJsonPreservesEveryResourceAndIsCopiedToOutput(string culture, string resource)
    {
        var source = Path.Combine(ProjectDirectory(), "Localization", $"ui.{culture}.json");
        var output = Path.Combine(AppContext.BaseDirectory, "Localization", $"ui.{culture}.json");
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(output));
        var entries = XDocument.Load(Path.Combine(ProjectDirectory(), "Resources", resource))
            .Root!.Elements("data").ToDictionary(item => item.Attribute("name")!.Value, item => item.Element("value")!.Value);
        var json = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(output))!;
        Assert.Equal(entries.Count, json.Count);
        foreach (var (key, value) in entries)
            Assert.Equal(value, json[key]);
    }

    [Fact]
    public void RuntimeEditsRefreshNewScopesButKeepExistingScopesConsistent()
    {
        using var culture = new CultureScope("en-US");
        using var files = new LanguageFiles();
        using var services = files.Services();
        using var first = services.CreateScope();
        var original = first.ServiceProvider.GetRequiredService<UiText>();
        Assert.Equal("Language", original["Language.Title"]);

        files.Edit("en-US", values => values["Language.Title"] = "Live language");
        files.Edit("de-DE", values => values["Language.Title"] = "Live Sprache");
        using var second = services.CreateScope();
        var updated = second.ServiceProvider.GetRequiredService<UiText>();
        Assert.Equal("Live language", updated["Language.Title"]);
        Assert.Equal("Language", original["Language.Title"]);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
        Assert.Equal("Live Sprache", updated["Language.Title"]);
        Assert.Equal("Sprache", original["Language.Title"]);
        Assert.False(updated.HasResourceReloadFailure);
    }

    [Fact]
    public void HashDetectionIncludesSameLengthChangesWithPreservedTimestamps()
    {
        using var culture = new CultureScope("en-US");
        using var files = new LanguageFiles();
        files.Edit("en-US", values => values["Language.Title"] = "Stage one");
        using var services = files.Services();
        using var first = services.CreateScope();
        Assert.Equal("Stage one", first.ServiceProvider.GetRequiredService<UiText>()["Language.Title"]);
        var path = files.PathFor("en-US");
        var modified = File.GetLastWriteTimeUtc(path);
        var length = new FileInfo(path).Length;
        files.Edit("en-US", values => values["Language.Title"] = "Stage two");
        File.SetLastWriteTimeUtc(path, modified);
        Assert.Equal(length, new FileInfo(path).Length);
        using var second = services.CreateScope();
        Assert.Equal("Stage two", second.ServiceProvider.GetRequiredService<UiText>()["Language.Title"]);
    }

    [Fact]
    public async Task HttpRequestsUseEditedFilesWithoutRebuildingOrRestartingTheHost()
    {
        using var culture = new CultureScope("en-US");
        using var files = new LanguageFiles();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory, Args = []
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddUiLocalization();
        builder.Services.AddSingleton(files.Store);
        await using var app = builder.Build();
        app.UseRequestLocalization();
        app.MapGet("/text", (UiText text) => new TextProbe(text["Language.Title"], text.HasResourceReloadFailure));
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            Assert.Equal(new TextProbe("Language", false), await client.GetFromJsonAsync<TextProbe>("/text"));
            files.Edit("en-US", values => values["Language.Title"] = "Live HTTP English");
            Assert.Equal(new TextProbe("Live HTTP English", false), await client.GetFromJsonAsync<TextProbe>("/text"));
            client.DefaultRequestHeaders.Add("Cookie",
                $"{UiLocalization.CookieName}={Uri.EscapeDataString("c=en-US|uic=de-DE")}");
            files.Edit("de-DE", values => values["Language.Title"] = "Live HTTP Deutsch");
            Assert.Equal(new TextProbe("Live HTTP Deutsch", false), await client.GetFromJsonAsync<TextProbe>("/text"));
            files.Write("de-DE", "{");
            Assert.Equal(new TextProbe("Live HTTP Deutsch", true), await client.GetFromJsonAsync<TextProbe>("/text"));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingGermanTranslationsUseCurrentRuntimeEnglishAndAreReported(bool empty)
    {
        using var culture = new CultureScope("de-DE");
        using var files = new LanguageFiles();
        files.Edit("en-US", values => values["Language.Apply"] = "Apply live language");
        files.Edit("de-DE", values =>
        {
            if (empty) values["Language.Apply"] = "   ";
            else values.Remove("Language.Apply");
        });
        using var services = files.Services();
        using var scope = services.CreateScope();
        var text = scope.ServiceProvider.GetRequiredService<UiText>();
        Assert.Equal("Apply live language", text["Language.Apply"]);
        Assert.Equal("Sprache", text["Language.Title"]);
        Assert.Equal("de-DE", CultureInfo.CurrentUICulture.Name);
        Assert.Contains(LogLevel.Warning, files.Logger.Levels);
        Assert.False(text.HasResourceReloadFailure);
    }

    [Fact]
    public void InvalidReloadIsAtomicReportedOnceAndRecovers()
    {
        using var culture = new CultureScope("en-US");
        using var files = new LanguageFiles();
        using var services = files.Services();
        using var first = services.CreateScope();
        var original = first.ServiceProvider.GetRequiredService<UiText>();
        files.Edit("en-US", values => values["Language.Title"] = "Updated language");
        files.Write("de-DE", """{"Language.Title":"{0}"}""");
        using var failed = services.CreateScope();
        var retained = failed.ServiceProvider.GetRequiredService<UiText>();
        Assert.Equal("Language", retained["Language.Title"]);
        Assert.True(retained.HasResourceReloadFailure);
        Assert.False(original.HasResourceReloadFailure);
        using var repeated = services.CreateScope();
        Assert.True(repeated.ServiceProvider.GetRequiredService<UiText>().HasResourceReloadFailure);
        Assert.Single(files.Logger.Levels, level => level == LogLevel.Error);

        files.Write("de-DE", """{"Language.Title":"Aktuelle Sprache"}""");
        using var recovered = services.CreateScope();
        var latest = recovered.ServiceProvider.GetRequiredService<UiText>();
        Assert.Equal("Updated language", latest["Language.Title"]);
        Assert.False(latest.HasResourceReloadFailure);
        Assert.True(retained.HasResourceReloadFailure);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
        Assert.Equal("Aktuelle Sprache", latest["Language.Title"]);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public void MissingFileRetainsLastGoodSnapshotAndRestoringIdenticalBytesClearsError(string missing)
    {
        using var files = new LanguageFiles();
        using var services = files.Services();
        using var first = services.CreateScope();
        _ = first.ServiceProvider.GetRequiredService<UiText>();
        var bytes = File.ReadAllBytes(files.PathFor(missing));
        File.Delete(files.PathFor(missing));
        using var failed = services.CreateScope();
        Assert.True(failed.ServiceProvider.GetRequiredService<UiText>().HasResourceReloadFailure);
        File.WriteAllBytes(files.PathFor(missing), bytes);
        using var restored = services.CreateScope();
        Assert.False(restored.ServiceProvider.GetRequiredService<UiText>().HasResourceReloadFailure);
        Assert.Contains(LogLevel.Error, files.Logger.Levels);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("""{"Language.Title":"Sprache","Language.Title":"Duplicate"}""")]
    [InlineData("""{"Language.Title":null}""")]
    [InlineData("""{"Language.Title":7}""")]
    [InlineData("""{"Language.Title":{}}""")]
    [InlineData("""{"Unknown.Key":"Unknown"}""")]
    [InlineData("""{"Language.Title":"{"}""")]
    [InlineData("""{"Language.Title":"{0}"}""")]
    [InlineData("""{"Language.Title":"{32}"}""")]
    [InlineData("""{"Language.Title":"Sprache",}""")]
    public void InitialInvalidJsonFailsExplicitly(string json)
    {
        using var files = new LanguageFiles();
        files.Write("de-DE", json);
        Assert.Throws<InvalidOperationException>(files.Store.Validate);
        Assert.Contains(LogLevel.Critical, files.Logger.Levels);
    }

    [Theory]
    [InlineData("missing-key")]
    [InlineData("empty-value")]
    [InlineData("missing-file")]
    [InlineData("oversized-file")]
    [InlineData("oversized-text")]
    [InlineData("invalid-utf8")]
    public void InitialInvalidEnglishCannotSilentlyUseEmbeddedResources(string failure)
    {
        using var files = new LanguageFiles();
        switch (failure)
        {
            case "missing-key": files.Edit("en-US", values => values.Remove("Language.Title")); break;
            case "empty-value": files.Edit("en-US", values => values["Language.Title"] = " "); break;
            case "missing-file": File.Delete(files.PathFor("en-US")); break;
            case "oversized-file":
                File.WriteAllBytes(files.PathFor("en-US"), new byte[JsonUiLanguageStore.MaximumFileBytes + 1]);
                break;
            case "oversized-text":
                files.Edit("en-US", values => values["Language.Title"] = new string('a', JsonUiLanguageStore.MaximumTextLength + 1));
                break;
            case "invalid-utf8": File.WriteAllBytes(files.PathFor("en-US"), [0xff]); break;
            default: throw new ArgumentOutOfRangeException(nameof(failure));
        }
        Assert.Throws<InvalidOperationException>(files.Store.Validate);
        Assert.Contains(LogLevel.Critical, files.Logger.Levels);
    }

    [Fact]
    public void ExactFileSizeLimitIsAccepted()
    {
        using var files = new LanguageFiles();
        var path = files.PathFor("en-US");
        using (var file = new FileStream(path, FileMode.Append))
        {
            var padding = Enumerable.Repeat((byte)' ',
                JsonUiLanguageStore.MaximumFileBytes - checked((int)file.Length)).ToArray();
            file.Write(padding);
        }
        Assert.Equal(JsonUiLanguageStore.MaximumFileBytes, new FileInfo(path).Length);
        files.Store.Validate();
        Assert.DoesNotContain(LogLevel.Critical, files.Logger.Levels);
    }

    [Fact]
    public void ExactTextLengthLimitIsAccepted()
    {
        using var culture = new CultureScope("en-US");
        using var files = new LanguageFiles();
        var text = new string('a', JsonUiLanguageStore.MaximumTextLength);
        files.Edit("en-US", values => values["Language.Title"] = text);
        using var services = files.Services();
        using var scope = services.CreateScope();
        Assert.Equal(text, scope.ServiceProvider.GetRequiredService<UiText>()["Language.Title"]);
    }

    [Fact]
    public void Utf8BomIsSupportedAndFormatArgumentsArePreserved()
    {
        using var culture = new CultureScope("de-DE");
        using var files = new LanguageFiles();
        const string key = "Rules.DurationMinutes.One";
        files.Edit("de-DE", values => values[key] = "Live {0} / {0}");
        var path = files.PathFor("de-DE");
        File.WriteAllBytes(path, Encoding.UTF8.GetPreamble().Concat(File.ReadAllBytes(path)).ToArray());
        using var services = files.Services();
        using var scope = services.CreateScope();
        var localizer = scope.ServiceProvider.GetRequiredService<IStringLocalizer<UiResources>>();
        Assert.Equal("Live 42 / 42", localizer[key, 42].Value);
        Assert.Equal("Live 42 / 42", scope.ServiceProvider.GetRequiredService<UiText>().Format(key, 42));
        Assert.True(localizer["Unknown.Key"].ResourceNotFound);
    }

    [Theory]
    [InlineData("Plain text", new int[0])]
    [InlineData("{{0}}", new int[0])]
    [InlineData("{0} {2} {0}", new[] { 0, 2 })]
    [InlineData("{{{2}}} {0,12:N0} {2,-10:u}", new[] { 0, 2 })]
    [InlineData("{0:yyyy-MM-dd HH:mm:ss}", new[] { 0 })]
    [InlineData("{31,4096:N100}", new[] { 31 })]
    public void RuntimeFormatScannerHandlesEscapesAlignmentAndRepeatedIndexes(string value, int[] expected) =>
        Assert.Equal(expected, JsonUiLanguageStore.ParameterIndexes(value).Order());

    [Fact]
    public void RuntimeScannerMatchesActualFormattedParameterUsageForAllBundledTexts()
    {
        foreach (var culture in new[] { "en-US", "de-DE" })
        {
            var texts = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Localization", $"ui.{culture}.json")))!;
            foreach (var value in texts.Values)
                Assert.Equal(LocalizationResourceAcceptanceTests.ParameterIndexes(value),
                    JsonUiLanguageStore.ParameterIndexes(value).Order());
        }
    }

    [Theory]
    [InlineData("{")]
    [InlineData("}")]
    [InlineData("{x}")]
    [InlineData("{0")]
    [InlineData("{-1}")]
    [InlineData("{0:broken{format}}")]
    [InlineData("{32}")]
    [InlineData("{999999999}")]
    [InlineData("{0,999999999}")]
    [InlineData("{0,-4097}")]
    [InlineData("{0:N999999999}")]
    public void RuntimeFormatValidationDoesNotRenderDangerousOrMalformedFormats(string value) =>
        Assert.Throws<FormatException>(() => JsonUiLanguageStore.ParameterIndexes(value));

    [Fact]
    public void RuntimeEnglishEditsCannotChangeTrustedQueryTemplateCatalog()
    {
        using var culture = new CultureScope("en-US");
        using var files = new LanguageFiles();
        var entry = QueryTemplateCatalog.Entries.First();
        var canonical = UiResources.GetEnglish(entry.NameResourceKey);
        files.Edit("en-US", values => values[entry.NameResourceKey] = "Editable display name");
        using var services = files.Services();
        using var scope = services.CreateScope();
        Assert.Equal("Editable display name", scope.ServiceProvider.GetRequiredService<UiText>()[entry.NameResourceKey]);
        Assert.Equal(canonical, UiResources.GetEnglish(entry.NameResourceKey));
        Assert.Equal(entry.Name, canonical);
        UiResources.ValidateEnglish();
    }

    [Fact]
    public async Task ReloadFailureIsRenderedAsVisibleSelectorWarning()
    {
        using var culture = new CultureScope("de-DE");
        using var files = new LanguageFiles();
        files.Store.Validate();
        files.Write("de-DE", "{");
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddUiLocalization();
        builder.Services.AddSingleton(files.Store);
        builder.Services.AddScoped<NavigationManager, TestNavigation>();
        await using var app = builder.Build();
        await using var scope = app.Services.CreateAsyncScope();
        await using var renderer = new HtmlRenderer(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<LanguageSelector>()).ToHtmlString());
        Assert.Contains("data-language-resource-error role=\"alert\"", html);
        Assert.Contains("zuletzt geprüften Texte", WebUtility.HtmlDecode(html));
    }

    private static string ProjectDirectory([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "MonitoringApp"));

    private sealed record TextProbe(string Title, bool HasReloadFailure);

    private sealed class LanguageFiles : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), $"MonitoringApp-ui-language-{Guid.NewGuid():N}");
        public RecordingLogger Logger { get; } = new();
        public JsonUiLanguageStore Store { get; }

        public LanguageFiles()
        {
            Directory.CreateDirectory(directory);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Localization", "languages.json"),
                Path.Combine(directory, "languages.json"));
            foreach (var culture in new[] { "en-US", "de-DE" })
                File.Copy(Path.Combine(AppContext.BaseDirectory, "Localization", $"ui.{culture}.json"), PathFor(culture));
            Store = new JsonUiLanguageStore(directory, Logger);
        }

        public string PathFor(string culture) => Path.Combine(directory, $"ui.{culture}.json");
        public void Write(string culture, string text) => File.WriteAllText(PathFor(culture), text, new UTF8Encoding(false));
        public void Edit(string culture, Action<Dictionary<string, string>> edit)
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(PathFor(culture)))!;
            edit(values);
            Write(culture, JsonSerializer.Serialize(values));
        }
        public ServiceProvider Services() => new ServiceCollection().AddLogging().AddUiLocalization()
            .AddSingleton(Store).BuildServiceProvider();
        public void Dispose() => Directory.Delete(directory, recursive: true);
    }

    private sealed class RecordingLogger : ILogger<JsonUiLanguageStore>
    {
        public List<LogLevel> Levels { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Levels.Add(level);
    }

    private sealed class CultureScope(string culture) : IDisposable
    {
        private readonly CultureInfo previous = Set(culture);
        private static CultureInfo Set(string culture)
        {
            var previous = CultureInfo.CurrentUICulture;
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            return previous;
        }
        public void Dispose() => CultureInfo.CurrentUICulture = previous;
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/admin/settings");
    }
}
