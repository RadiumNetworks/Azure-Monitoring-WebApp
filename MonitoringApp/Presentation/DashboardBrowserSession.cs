using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MonitoringApp;

public enum DashboardBrowserPhase
{
    Import, Initialize, Cleanup
}

public sealed class DashboardBrowserSession<T>(
    IJSRuntime javascript, ILogger logger, string modulePath, T component, CancellationToken lifetimeToken)
    : IAsyncDisposable where T : class
{
    private readonly CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
    private IJSObjectReference? module;
    private DotNetObjectReference<T>? reference;
    private ElementReference root;
    private bool cleanupRequired;
    private bool handlersDetached;
    private bool disposed;
    private Task? setupTask;

    public bool Initialized { get; private set; }
    public bool IsInitializing { get; private set; }
    public DashboardBrowserPhase? ErrorPhase { get; private set; }

    public string? ErrorMessage(UiText text, string resourcePrefix) => ErrorPhase switch
    {
        null => null,
        DashboardBrowserPhase.Import => text[resourcePrefix + "ImportFailed"],
        DashboardBrowserPhase.Cleanup => text[resourcePrefix + "CleanupFailed"],
        _ => text[resourcePrefix + "InitializeFailed"]
    };

    public Task InitializeAsync(ElementReference element, Func<Task> stateChanged)
    {
        if (disposed || IsInitializing || Initialized) return Task.CompletedTask;
        setupTask = InitializeCoreAsync(element, stateChanged);
        return setupTask;
    }

    private async Task InitializeCoreAsync(ElementReference element, Func<Task> stateChanged)
    {
        IsInitializing = true;
        var phase = DashboardBrowserPhase.Cleanup;
        try
        {
            await stateChanged();
            if (module is not null)
            {
                if (cleanupRequired && !handlersDetached)
                {
                    await module.InvokeVoidAsync("dispose", lifetime.Token, root);
                    handlersDetached = true;
                }
                await module.DisposeAsync();
                module = null;
                reference?.Dispose();
                reference = null;
            }
            phase = DashboardBrowserPhase.Import;
            lifetime.Token.ThrowIfCancellationRequested();
            module = await javascript.InvokeAsync<IJSObjectReference>("import", lifetime.Token, modulePath)
                ?? throw new InvalidOperationException("The dashboard browser module was not returned.");
            cleanupRequired = false;
            handlersDetached = false;
            if (disposed || lifetime.IsCancellationRequested) return;
            root = element;
            reference = DotNetObjectReference.Create(component);
            cleanupRequired = true;
            phase = DashboardBrowserPhase.Initialize;
            await module.InvokeVoidAsync("initialize", lifetime.Token, reference, root);
            if (disposed || lifetime.IsCancellationRequested) return;
            Initialized = true;
            ErrorPhase = null;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Dashboard browser setup for {Module} failed at {Phase}.", modulePath, phase);
            if (!disposed && !lifetime.IsCancellationRequested) ErrorPhase = phase;
        }
        finally
        {
            IsInitializing = false;
        }
        if (!disposed && !lifetime.IsCancellationRequested) await stateChanged();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await lifetime.CancelAsync();
        if (setupTask is not null) await setupTask;
        if (module is not null)
        {
            if (cleanupRequired && !handlersDetached)
            {
                try { await module.InvokeVoidAsync("dispose", root); }
                catch (JSDisconnectedException) { }
                catch (Exception exception) { logger.LogError(exception, "Could not detach dashboard browser handlers for {Module}.", modulePath); }
            }
            try { await module.DisposeAsync(); }
            catch (JSDisconnectedException) { }
            catch (Exception exception) { logger.LogError(exception, "Could not release dashboard browser module {Module}.", modulePath); }
            module = null;
        }
        reference?.Dispose();
        reference = null;
        Initialized = false;
        lifetime.Dispose();
    }
}
