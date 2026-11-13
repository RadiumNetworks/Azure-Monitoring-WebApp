using System.Text;
using System.Text.Json;
using AlertConsole.Configuration;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;

namespace PlaywrightDemo.Tests;

[TestFixture]
[Category("E2E")]
[NonParallelizable]
public sealed class UiLanguageInteractionTests : PageTest
{
    private string baseUrl = "";
    private string directory = "";
    private readonly Dictionary<string, byte[]> originals = [];
    private Dictionary<string, string> english = [];
    private ILocator Selector => Page.Locator("[data-language-selector]");
    private ILocator Panel => Page.Locator("[data-language-administration]");
    private string Text(string key) => english[key];

    [SetUp]
    public async Task OpenIsolatedLocalPackage()
    {
        directory = Environment.GetEnvironmentVariable("ALERT_CONSOLE_LOCALIZATION_DIRECTORY") ?? "";
        if (Environment.GetEnvironmentVariable("ALLOW_MUTATION_TESTS") != "true" || directory.Length == 0)
            Assert.Ignore("Requires ALLOW_MUTATION_TESTS=true and an isolated published Localization directory.");
        baseUrl = AlertConsoleUrlResolver.Resolve().TrimEnd('/');
        Assert.That(new Uri(baseUrl).IsLoopback, Is.True, "Language mutation tests only target a local package.");
        directory = Path.GetFullPath(directory);
        Assert.That(File.Exists(Path.Combine(directory, "..", "MonitoringApp.dll")), Is.True,
            "Use the Localization directory of an isolated publish, never the source files.");
        originals.Clear();
        foreach (var file in new[] { "ui.en-US.json", "ui.de-DE.json", "languages.json" })
            originals.Add(file, await File.ReadAllBytesAsync(Path.Combine(directory, file)));
        english = JsonSerializer.Deserialize<Dictionary<string, string>>(originals["ui.en-US.json"])!;
        await Page.SetViewportSizeAsync(1440, 1000);
    }

    [TearDown]
    public void RestorePackage()
    {
        foreach (var (file, bytes) in originals)
            Write(file, bytes);
        originals.Clear();
    }

    [Test]
    public async Task LanguageSwitchProtectsKqlDraftAndPersistsAcrossTabs()
    {
        await Open("/kql");
        const string query = "print Value = 1.5, Timestamp = datetime(2026-11-13)";
        var editor = Page.Locator("textarea");
        await editor.FillAsync(query);
        await Selector.Locator("select").SelectOptionAsync("de-DE");
        await Selector.GetByRole(AriaRole.Button, new() { Name = Text("Language.Apply"), Exact = true }).ClickAsync();
        var dialog = Selector.Locator("dialog");
        await Expect(dialog).ToBeVisibleAsync();
        await dialog.Locator("[data-language-cancel]").ClickAsync();
        await Expect(dialog).Not.ToBeVisibleAsync();
        await Expect(editor).ToHaveValueAsync(query);
        await Expect(Selector.Locator("select")).ToHaveValueAsync("en-US");
        await Selector.Locator("select").SelectOptionAsync("de-DE");
        await Selector.GetByRole(AriaRole.Button, new() { Name = Text("Language.Apply"), Exact = true }).ClickAsync();
        await dialog.Locator("[data-language-continue]").ClickAsync();
        await Expect(Selector).ToHaveAttributeAsync("data-current-culture", "de-DE");
        await Expect(Page).ToHaveURLAsync(baseUrl + "/kql");
        await Expect(Page.Locator("textarea")).ToHaveValueAsync("");
        var another = await Context.NewPageAsync();
        try
        {
            await another.GotoAsync(baseUrl + "/queries");
            await Expect(another.Locator("[data-language-selector]")).ToHaveAttributeAsync("data-current-culture", "de-DE");
        }
        finally { await another.CloseAsync(); }
    }

    [Test]
    public async Task AdminUploadRequiresReviewAndCanRestoreAfterActivation()
    {
        await Open("/admin/settings");
        await Panel.GetByRole(AriaRole.Button, new() { Name = Text("LanguageAdmin.Add"), Exact = true }).ClickAsync();
        var rows = Panel.Locator("form > fieldset > fieldset");
        await Expect(rows).ToHaveCountAsync(3);
        var row = rows.Nth(2);
        await row.GetByLabel(Text("LanguageAdmin.Culture"), new() { Exact = true }).FillAsync("fr-FR");
        await row.GetByLabel(Text("LanguageAdmin.Name"), new() { Exact = true }).FillAsync("Francais - acceptance");
        await row.Locator("input[type=file]").SetInputFilesAsync(new FilePayload
        {
            Name = "french.json", MimeType = "application/json",
            Buffer = Encoding.UTF8.GetBytes("""{"Language.Title":"Langue de test"}""")
        });
        await Expect(row.GetByLabel(Text("LanguageAdmin.File"), new() { Exact = true }))
            .ToHaveValueAsync(new Regex(@"^ui-upload-[a-f0-9]+\.json$"));
        await Panel.GetByRole(AriaRole.Combobox, new() { Name = Text("LanguageAdmin.Default"), Exact = true }).SelectOptionAsync("fr-FR");
        await Panel.GetByRole(AriaRole.Button, new() { Name = Text("LanguageAdmin.Check"), Exact = true }).ClickAsync();
        var preview = Panel.Locator("[data-language-preview]");
        await Expect(preview).ToBeVisibleAsync();
        Assert.That(await File.ReadAllBytesAsync(Path.Combine(directory, "languages.json")),
            Is.EqualTo(originals["languages.json"]), "Preview must not write the registry.");
        var activate = preview.GetByRole(AriaRole.Button, new() { Name = Text("LanguageAdmin.Activate"), Exact = true });
        await Expect(activate).ToBeDisabledAsync();
        await preview.Locator("input[type=checkbox]").CheckAsync();
        await activate.ClickAsync();
        await Expect(Panel).ToContainTextAsync(Text("LanguageAdmin.Activated"));
        await Expect(Selector).ToHaveAttributeAsync("data-current-culture", "en-US");
        await using var fresh = await Browser.NewContextAsync(new() { Locale = "de-DE" });
        var freshPage = await fresh.NewPageAsync();
        await freshPage.GotoAsync(baseUrl + "/queries");
        await Expect(freshPage.Locator("[data-language-selector]")).ToHaveAttributeAsync("data-current-culture", "fr-FR");
        await Expect(freshPage.Locator("[data-language-selector] label")).ToContainTextAsync("Langue de test");
        await Panel.GetByRole(AriaRole.Button, new() { Name = Text("LanguageAdmin.CheckPrevious"), Exact = true }).ClickAsync();
        await Expect(preview).ToContainTextAsync("en-US");
        await preview.Locator("input[type=checkbox]").CheckAsync();
        await preview.GetByRole(AriaRole.Button, new() { Name = Text("LanguageAdmin.Activate"), Exact = true }).ClickAsync();
        await Expect(Panel).ToContainTextAsync(Text("LanguageAdmin.Activated"));
        await freshPage.ReloadAsync();
        await Expect(freshPage.Locator("[data-language-selector]")).ToHaveAttributeAsync("data-current-culture", "en-US");
        await Expect(freshPage.Locator("[data-language-selector] select option")).ToHaveCountAsync(2);
    }

    [Test]
    public async Task InvalidUploadAndConcurrentChangesCannotBeActivated()
    {
        await Open("/admin/settings");
        var german = Panel.Locator("form > fieldset > fieldset").Nth(1);
        await german.Locator("input[type=file]").SetInputFilesAsync(new FilePayload
        {
            Name = "invalid.json", MimeType = "application/json",
            Buffer = Encoding.UTF8.GetBytes("""{"Unknown.Acceptance.Key":42}""")
        });
        await Expect(german.GetByLabel(Text("LanguageAdmin.File"), new() { Exact = true }))
            .ToHaveValueAsync(new Regex(@"^ui-upload-"));
        await Panel.GetByRole(AriaRole.Button, new() { Name = Text("LanguageAdmin.Check"), Exact = true }).ClickAsync();
        var preview = Panel.Locator("[data-language-preview]");
        await Expect(preview).ToContainTextAsync(Text("LanguageAdmin.InvalidPreview"));
        await Expect(preview.Locator("input[type=checkbox]")).ToBeDisabledAsync();
        Assert.That(await File.ReadAllBytesAsync(Path.Combine(directory, "languages.json")),
            Is.EqualTo(originals["languages.json"]));
        await Panel.GetByRole(AriaRole.Button, new() { Name = Text("LanguageAdmin.Load"), Exact = true }).ClickAsync();
        await Panel.GetByRole(AriaRole.Combobox, new() { Name = Text("LanguageAdmin.Default"), Exact = true }).SelectOptionAsync("de-DE");
        await Panel.GetByRole(AriaRole.Button, new() { Name = Text("LanguageAdmin.Check"), Exact = true }).ClickAsync();
        await Expect(preview).ToBeVisibleAsync();
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(originals["ui.de-DE.json"])!;
        values["Language.Title"] = "Acceptance conflict";
        Write("ui.de-DE.json", JsonSerializer.SerializeToUtf8Bytes(values));
        await preview.Locator("input[type=checkbox]").CheckAsync();
        await preview.GetByRole(AriaRole.Button, new() { Name = Text("LanguageAdmin.Activate"), Exact = true }).ClickAsync();
        await Expect(Panel).ToContainTextAsync(Text("LanguageAdmin.Errors.Conflict"));
        await Expect(Panel.GetByRole(AriaRole.Button, new() { Name = Text("LanguageAdmin.Check"), Exact = true })).ToBeDisabledAsync();
        Assert.That(await File.ReadAllBytesAsync(Path.Combine(directory, "languages.json")),
            Is.EqualTo(originals["languages.json"]));
    }

    [Test]
    public async Task RuntimeFileReloadRetainsLastGoodTextsAndRecoversWithoutRestart()
    {
        await Open("/queries");
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(originals["ui.en-US.json"])!;
        values["Language.Title"] = "LangTest";
        var stamp = File.GetLastWriteTimeUtc(Path.Combine(directory, "ui.en-US.json"));
        Write("ui.en-US.json", JsonSerializer.SerializeToUtf8Bytes(values));
        File.SetLastWriteTimeUtc(Path.Combine(directory, "ui.en-US.json"), stamp);
        await Expect(Selector.Locator("label")).ToContainTextAsync("Language");
        await Page.ReloadAsync();
        await Expect(Selector.Locator("label")).ToContainTextAsync("LangTest");
        Write("ui.en-US.json", Encoding.UTF8.GetBytes("""{"Language.Title":"Incomplete English"}"""));
        await Page.ReloadAsync();
        await Expect(Selector.Locator("[data-language-resource-error]")).ToBeVisibleAsync();
        await Expect(Selector.Locator("label")).ToContainTextAsync("LangTest");
        Write("ui.en-US.json", originals["ui.en-US.json"]);
        await Page.ReloadAsync();
        await Expect(Selector.Locator("[data-language-resource-error]")).ToHaveCountAsync(0);
        await Expect(Selector.Locator("label")).ToContainTextAsync("Language");
    }

    [TestCase("broken", "de-DE")]
    [TestCase("c=en-US|uic=fr-FR", "de-DE")]
    [TestCase("c=en-US|uic=en-US", "en-US")]
    public async Task DefaultChangeRejectsUnavailableCookiesButPreservesValidSelections(string cookie, string expected)
    {
        var registry = JsonNode.Parse(originals["languages.json"])!;
        registry["defaultCulture"] = "de-DE";
        Write("languages.json", Encoding.UTF8.GetBytes(registry.ToJsonString()));
        await Context.AddCookiesAsync([new Cookie
        {
            Name = ".MonitoringApp.UiCulture", Value = Uri.EscapeDataString(cookie), Url = baseUrl
        }]);
        await Open("/queries");
        await Expect(Selector).ToHaveAttributeAsync("data-current-culture", expected);
    }

    [Test]
    public async Task MobileLanguageControlsRemainUsableWithoutHorizontalOverflow()
    {
        await Page.SetViewportSizeAsync(390, 844);
        await Open("/admin/settings");
        await Expect(Selector.Locator("select")).ToBeInViewportAsync();
        Assert.That(await Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth + 1"), Is.True);
        await Selector.Locator("select").SelectOptionAsync("de-DE");
        await Selector.GetByRole(AriaRole.Button, new() { Name = Text("Language.Apply"), Exact = true }).ClickAsync();
        await Expect(Selector).ToHaveAttributeAsync("data-current-culture", "de-DE");
        Assert.That(await Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth + 1"), Is.True);
    }

    private async Task Open(string path)
    {
        await Page.GotoAsync(baseUrl + path);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Selector.Locator("select")).ToBeEnabledAsync();
    }

    private void Write(string file, byte[] bytes)
    {
        var target = Path.Combine(directory, file);
        if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) return;
        var temporary = Path.Combine(directory, ".acceptance-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Replace(temporary, target, destinationBackupFileName: null);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
