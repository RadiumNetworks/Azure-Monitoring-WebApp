using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MonitoringApp.Components;
using MonitoringApp.Components.Pages;
using ErrorPage = MonitoringApp.Components.Pages.Error;

namespace MonitoringApp.Tests;

public sealed class ErrorAndDiagnosticLocalizationTests
{
    [Theory]
    [InlineData("en-US", "Error", "Something went wrong", "The request could not be completed.")]
    [InlineData("de-DE", "Fehler", "Etwas ist schiefgelaufen", "Die Anfrage konnte nicht abgeschlossen werden.")]
    public async Task AnonymousErrorPageLocalizesHeadingDescriptionAndActualHeadTitle(
        string culture, string title, string heading, string description)
    {
        using var selection = new AnalysisResultLocalizationTests.DisplayCulture(culture);
        await using var services = new ServiceCollection().AddLogging().AddUiLocalization()
            .AddSingleton<IJSRuntime, NoJavaScript>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var head = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<HeadOutlet>());
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<ErrorPage>());
        var html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
        Assert.Contains($"<h1>{heading}</h1>", html);
        Assert.Contains($"<p class=\"subtitle\">{description}</p>", html);
        Assert.Contains("<p class=\"eyebrow\">Alert Console</p>", html);
        Assert.Equal($"<title>{title}</title>", await renderer.Dispatcher.InvokeAsync(head.ToHtmlString));
        Assert.Equal("/error", Assert.Single(typeof(ErrorPage).GetCustomAttributes<RouteAttribute>()).Template);
        Assert.NotNull(typeof(ErrorPage).GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Empty(typeof(ErrorPage).GetCustomAttributes<AuthorizeAttribute>());
        Assert.DoesNotContain("Exception", html);
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task DiagnosticUiTranslatesKnownCodesOnlyAndPreservesPayloadAndVisibility(string culture)
    {
        using var selection = new AnalysisResultLocalizationTests.DisplayCulture(culture);
        await using var services = new ServiceCollection().AddLogging().AddUiLocalization().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        foreach (var status in Enum.GetValues<DiagnosticStatus>().Concat([(DiagnosticStatus)99, (DiagnosticStatus)(-1)]))
        foreach (var duration in new[] { -1L, 0, 1, 2, 1234, long.MaxValue })
        foreach (var correlation in new[] { null, "", " \t", "trace-<original> ä\r\n&\"id\"" })
        {
            var step = new DiagnosticStep("Succeeded <original name>", status, duration,
                "Failed <script>original & diagnostic</script>", correlation);
            var original = JsonSerializer.Serialize(step);
            var root = await renderer.Dispatcher.InvokeAsync(() =>
                renderer.RenderComponentAsync<DiagnosticStepView>(ParameterView.FromDictionary(
                    new Dictionary<string, object?> { ["Step"] = step })));
            var raw = await renderer.Dispatcher.InvokeAsync(root.ToHtmlString);
            var html = WebUtility.HtmlDecode(raw);
            var label = status switch
            {
                DiagnosticStatus.Succeeded => culture == "de-DE" ? "Erfolgreich" : "Succeeded",
                DiagnosticStatus.Failed => culture == "de-DE" ? "Fehlgeschlagen" : "Failed",
                DiagnosticStatus.NotRun => culture == "de-DE" ? "Nicht ausgeführt" : "NotRun",
                DiagnosticStatus.Disabled => culture == "de-DE" ? "Deaktiviert" : "Disabled",
                _ => culture == "de-DE" ? "Unbekannter Status" : "Unknown status"
            };
            Assert.Contains($"<span>{label}</span>", html);
            Assert.Contains($"admin-diagnostic-{(Enum.IsDefined(status) ? status.ToString().ToLowerInvariant() : "unknown")}", raw);
            Assert.Contains(step.Name, html);
            Assert.Contains(step.Message, html);
            Assert.DoesNotContain("<script>", raw);
            Assert.DoesNotContain("<original>", raw);
            if (duration > 0)
                Assert.Contains($"<small>{duration.ToString(CultureInfo.CurrentCulture)} ms</small>", html);
            else
                Assert.DoesNotContain(" ms</small>", html);
            if (!string.IsNullOrWhiteSpace(correlation))
                Assert.Contains($"<small>{(culture == "de-DE" ? "Korrelation" : "Correlation")}: {correlation}</small>", html);
            else
                Assert.DoesNotContain(culture == "de-DE" ? "Korrelation:" : "Correlation:", html);
            Assert.Equal((duration > 0 ? 1 : 0) + (!string.IsNullOrWhiteSpace(correlation) ? 1 : 0),
                raw.Split("<small>", StringSplitOptions.None).Length - 1);
            Assert.Equal(original, JsonSerializer.Serialize(step));
        }
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData("en-US", "disabled")]
    [InlineData("de-DE", "disabled")]
    [InlineData("en-US", "secret-failed")]
    [InlineData("de-DE", "secret-failed")]
    [InlineData("en-US", "probe")]
    [InlineData("de-DE", "probe")]
    public async Task RealAdministrationDiagnosticsRenderLocalizedStatesWithoutChangingReportsOrAdapters(
        string culture, string outcome)
    {
        using var selection = new AnalysisResultLocalizationTests.DisplayCulture(culture);
        var options = outcome == "disabled" ? new CrossTenantLogAnalyticsOptions()
            : CrossTenantLogAnalyticsOptionsTests.CreateValid();
        var originalOptions = JsonSerializer.Serialize(options);
        var secret = new SecretStub(outcome == "secret-failed");
        var probe = new ProbeStub();
        var diagnostics = new LogAnalyticsConnectionDiagnostics(options, secret, probe);
        var activator = new DiagnosticActivator();
        await using var services = new ServiceCollection().AddLogging().AddUiLocalization()
            .AddSingleton(options).AddSingleton(diagnostics)
            .AddSingleton<IComponentActivator>(activator).BuildServiceProvider();
        await using var renderer = new StaticPageRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var root = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var page = renderer.BeginRenderingComponent(typeof(AdminLogAnalytics), ParameterView.Empty);
            await page.QuiescenceTask;
            return page;
        });
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await Assert.IsAssignableFrom<Task>(typeof(AdminLogAnalytics).GetMethod("RunDiagnosticsAsync", flags)!
                .Invoke(activator.Page, null));
            typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(activator.Page, null);
        });
        var report = Assert.IsType<LogAnalyticsDiagnosticsReport>(
            typeof(AdminLogAnalytics).GetField("report", flags)!.GetValue(activator.Page));
        var originalReport = JsonSerializer.Serialize(report);
        var html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
        var text = services.GetRequiredService<UiText>();
        Assert.Contains(text.Format("LogAnalyticsAdmin.CheckedAt", report.CheckedAt.ToString("yyyy-MM-dd HH:mm:ss")), html);
        Assert.Contains(text[$"Diagnostics.Status.{report.KeyVault.Status}"], html);
        Assert.Equal(outcome == "disabled" ? 0 : 1, secret.Calls);
        Assert.Equal(outcome == "probe" ? 1 : 0, probe.Calls);
        if (outcome == "disabled")
        {
            Assert.Equal(DiagnosticStatus.Disabled, report.KeyVault.Status);
            Assert.Empty(report.Workspaces);
            Assert.Contains(text["Diagnostics.Message.Disabled"], html);
        }
        else if (outcome == "secret-failed")
        {
            Assert.Equal(DiagnosticStatus.Failed, report.KeyVault.Status);
            Assert.Contains(text["Diagnostics.Message.KeyVaultFailed"], html);
            Assert.DoesNotContain("Failed <original detail>", html);
            Assert.Contains(text["Diagnostics.Status.NotRun"], html);
            Assert.All(report.Workspaces, workspace =>
            {
                Assert.Equal(DiagnosticStatus.NotRun, workspace.Token.Status);
                Assert.Equal(DiagnosticStatus.NotRun, workspace.Query.Status);
            });
        }
        else
        {
            Assert.Equal(DiagnosticStatus.Succeeded, report.KeyVault.Status);
            Assert.Contains(text["Diagnostics.Message.Succeeded"], html);
            Assert.Contains("Original <query failure>", html);
            Assert.Contains(text.Format("Diagnostics.Correlation", "trace-<original>"), html);
            Assert.Contains(text["Diagnostics.Status.Failed"], html);
            Assert.Equal(options.ApplicationClientId, probe.ApplicationClientId);
            Assert.Equal(TimeSpan.FromSeconds(options.DiagnosticTimeoutSeconds), probe.Timeout);
            Assert.Same(options.Workspaces[0], probe.Workspace);
        }
        Assert.Equal(originalOptions, JsonSerializer.Serialize(options));
        Assert.Equal(originalReport, JsonSerializer.Serialize(report));
        Assert.Equal("AdminAccess", Assert.Single(typeof(AdminLogAnalytics).GetCustomAttributes<AuthorizeAttribute>()).Policy);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task GeneratedStepProvenanceLocalizesOnlyMarkedMessagesAndKeepsJsonContract(string culture)
    {
        using var selection = new AnalysisResultLocalizationTests.DisplayCulture(culture);
        await using var services = new ServiceCollection().AddLogging().AddUiLocalization().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var text = services.GetRequiredService<UiText>();
        foreach (var code in Enum.GetValues<DiagnosticMessageCode>())
        {
            var status = code switch
            {
                DiagnosticMessageCode.Succeeded => DiagnosticStatus.Succeeded,
                DiagnosticMessageCode.Disabled => DiagnosticStatus.Disabled,
                DiagnosticMessageCode.KeyVaultRequired or DiagnosticMessageCode.TokenRequired => DiagnosticStatus.NotRun,
                _ => DiagnosticStatus.Failed
            };
            var original = new DiagnosticStep("original <name>", status, 1234, "original <private message>", "trace-<id>");
            var step = original with { NameCode = DiagnosticNameCode.TenantToken, MessageCode = code, HttpStatus = 403 };
            Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(step));
            var (name, state, duration, message, correlation) = step;
            Assert.Equal((original.Name, original.Status, original.DurationMilliseconds, original.Message, original.CorrelationId),
                (name, state, duration, message, correlation));
            var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<DiagnosticStepView>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["Step"] = step })));
            var html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
            var expected = code is DiagnosticMessageCode.KeyVaultRequestFailed or DiagnosticMessageCode.QueryRequestFailed
                ? text.Format($"Diagnostics.Message.{code}", 403) : text[$"Diagnostics.Message.{code}"];
            Assert.Contains(text["Diagnostics.Name.TenantToken"], html);
            Assert.Contains(expected, html);
            Assert.DoesNotContain(original.Message, html);
            Assert.DoesNotContain(original.Name, html);
            Assert.Contains(text.Format("Diagnostics.Correlation", "trace-<id>"), html);
            Assert.Contains("1234 ms", html);
            var incompatible = step with { Status = status == DiagnosticStatus.Succeeded ? DiagnosticStatus.Failed : DiagnosticStatus.Succeeded };
            Assert.Equal(text["Diagnostics.Message.Unavailable"], DiagnosticErrorPresentation.Message(incompatible, text));
            if (code is DiagnosticMessageCode.KeyVaultRequestFailed or DiagnosticMessageCode.QueryRequestFailed)
                Assert.Equal(text["Diagnostics.Message.Unavailable"], DiagnosticErrorPresentation.Message(step with { HttpStatus = null }, text));
            var restored = JsonSerializer.Deserialize<DiagnosticStep>(JsonSerializer.Serialize(step))!;
            Assert.Null(restored.NameCode);
            Assert.Null(restored.MessageCode);
            Assert.Null(restored.HttpStatus);
            Assert.Equal(original.Message, DiagnosticErrorPresentation.Message(restored, text));
        }
        foreach (var code in Enum.GetValues<DiagnosticNameCode>())
            Assert.Equal(text[$"Diagnostics.Name.{code}"], DiagnosticErrorPresentation.Name(
                new("own", DiagnosticStatus.Succeeded, 0, "own") { NameCode = code }, text));
        var unknown = new DiagnosticStep("private", DiagnosticStatus.Failed, 0, "private")
        { NameCode = (DiagnosticNameCode)999, MessageCode = (DiagnosticMessageCode)999 };
        Assert.Equal(text["Diagnostics.Name.Unknown"], DiagnosticErrorPresentation.Name(unknown, text));
        Assert.Equal(text["Diagnostics.Message.Unavailable"], DiagnosticErrorPresentation.Message(unknown, text));
        var lookalike = new DiagnosticStep("Tenant token", DiagnosticStatus.Succeeded, 0, "Succeeded.");
        Assert.Equal("Tenant token", DiagnosticErrorPresentation.Name(lookalike, text));
        Assert.Equal("Succeeded.", DiagnosticErrorPresentation.Message(lookalike, text));
        Assert.Equal("fr-FR", CultureInfo.CurrentCulture.Name);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task DiagnosticsPageKeepsReportOnFailuresAndBlocksConcurrentRuns(string culture)
    {
        using var selection = new AnalysisResultLocalizationTests.DisplayCulture(culture);
        var options = CrossTenantLogAnalyticsOptionsTests.CreateValid();
        var secret = new SecretStub(false);
        var probe = new ProbeStub();
        var diagnostics = new LogAnalyticsConnectionDiagnostics(options, secret, probe);
        var activator = new DiagnosticActivator();
        var logger = new DiagnosticLogger<AdminLogAnalytics>();
        await using var services = new ServiceCollection().AddLogging().AddUiLocalization()
            .AddSingleton(options).AddSingleton(diagnostics).AddSingleton<IComponentActivator>(activator)
            .AddSingleton<ILogger<AdminLogAnalytics>>(logger).BuildServiceProvider();
        await using var renderer = new StaticPageRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var root = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = renderer.BeginRenderingComponent(typeof(AdminLogAnalytics), ParameterView.Empty);
            await rendered.QuiescenceTask;
            return rendered;
        });
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Task Invoke() => renderer.Dispatcher.InvokeAsync(async () =>
        {
            await (Task)typeof(AdminLogAnalytics).GetMethod("RunDiagnosticsAsync", flags)!.Invoke(activator.Page, null)!;
            typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(activator.Page, null);
        });
        object? Field(string name) => typeof(AdminLogAnalytics).GetField(name, flags)!.GetValue(activator.Page);
        var fault = new IOException("private-diagnostic-provider");
        probe.Before = () => Task.FromException(fault);
        await Invoke();
        var text = services.GetRequiredService<UiText>();
        Assert.Null(Field("report"));
        var html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
        Assert.Contains(text["LogAnalyticsAdmin.Failed"], html);
        Assert.DoesNotContain(text["LogAnalyticsAdmin.NoReport"], html);
        Assert.DoesNotContain(fault.Message, html);
        Assert.Equal(LogLevel.Error, logger.Entries.Last().Level);
        Assert.Same(fault, logger.Entries.Last().Error);
        probe.Before = null;
        await Invoke();
        var report = Assert.IsType<LogAnalyticsDiagnosticsReport>(Field("report"));
        var json = JsonSerializer.Serialize(report);
        foreach (var error in new Exception[] { fault, new OperationCanceledException("private-cancellation") })
        {
            probe.Before = () => Task.FromException(error);
            await Invoke();
            Assert.Same(report, Field("report"));
            Assert.Equal(json, JsonSerializer.Serialize(report));
            Assert.Equal(text[error is OperationCanceledException ? "LogAnalyticsAdmin.Cancelled" : "LogAnalyticsAdmin.Failed"],
                Field("errorMessage"));
            Assert.Same(error, logger.Entries.Last().Error);
            Assert.Equal(error is OperationCanceledException ? LogLevel.Warning : LogLevel.Error, logger.Entries.Last().Level);
        }
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.Before = async () => { started.SetResult(); await release.Task; };
        var run = Invoke();
        await started.Task;
        var calls = probe.Calls;
        await Invoke();
        Assert.Equal(calls, probe.Calls);
        Assert.Equal(true, Field("isRunning"));
        release.SetResult();
        await run;
        Assert.Equal(false, Field("isRunning"));
        Assert.Null(Field("errorMessage"));
        Assert.NotSame(report, Field("report"));
    }

    private sealed class DiagnosticLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, Exception? Error)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((level, exception));
    }

    private sealed class SecretStub(bool fail) : ILogAnalyticsSecretProvider
    {
        public int Calls { get; private set; }
        public Task<string> GetClientSecretAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return fail ? Task.FromException<string>(new InvalidOperationException("Failed <original detail>"))
                : Task.FromResult(nameof(SecretStub));
        }
    }

    private sealed class ProbeStub : ILogAnalyticsWorkspaceProbe
    {
        public Func<Task>? Before { get; set; }
        public int Calls { get; private set; }
        public string? ApplicationClientId { get; private set; }
        public TimeSpan Timeout { get; private set; }
        public CrossTenantLogAnalyticsWorkspaceOptions? Workspace { get; private set; }
        public async Task<LogAnalyticsWorkspaceProbeResult> ProbeAsync(
            CrossTenantLogAnalyticsWorkspaceOptions workspace, string applicationClientId, string clientSecret,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls++;
            if (Before is not null) await Before();
            Workspace = workspace;
            ApplicationClientId = applicationClientId;
            Timeout = timeout;
            Assert.Equal(nameof(SecretStub), clientSecret);
            return new LogAnalyticsWorkspaceProbeResult(
                DiagnosticStep.Succeeded("Tenant token", TimeSpan.FromMilliseconds(1)),
                DiagnosticStep.Failed("Workspace query", TimeSpan.FromMilliseconds(2), "Original <query failure>", "trace-<original>"));
        }
    }

    private sealed class DiagnosticActivator : IComponentActivator
    {
        public AdminLogAnalytics? Page { get; private set; }
        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is AdminLogAnalytics page) Page = page;
            return component;
        }
    }

    private sealed class StaticPageRenderer(IServiceProvider services, ILoggerFactory loggerFactory)
        : Microsoft.AspNetCore.Components.HtmlRendering.Infrastructure.StaticHtmlRenderer(services, loggerFactory)
    {
        protected override IComponent ResolveComponentForRenderMode(
            Type componentType, int? parentComponentId, IComponentActivator componentActivator, IComponentRenderMode renderMode) =>
            renderMode is InteractiveServerRenderMode ? componentActivator.CreateInstance(componentType)
                : throw new NotSupportedException($"Unsupported render mode: {renderMode.GetType().Name}.");
    }

    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new InvalidOperationException("Static error-page rendering must not invoke JavaScript.");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            throw new InvalidOperationException("Static error-page rendering must not invoke JavaScript.");
    }
}
