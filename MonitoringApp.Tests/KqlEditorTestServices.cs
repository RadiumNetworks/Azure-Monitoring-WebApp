using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.JSInterop;

namespace MonitoringApp.Tests;

internal static class KqlEditorTestServices
{
    public static IServiceCollection AddKqlEditorTestServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IJSRuntime, KqlEditorJavaScript>();
        return services.AddKqlServices(new())
            .AddSingleton<ILogAnalyticsSecretProvider>(new KeyVaultLogAnalyticsSecretProvider(new()));
    }
}

internal sealed class KqlEditorJavaScript : IJSRuntime
{
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        InvokeAsync<TValue>(identifier, default, args);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        Assert.Equal("kqlCompletion.connect", identifier);
        Assert.Equal(2, args?.Length);
        return ValueTask.FromResult(default(TValue)!);
    }
}
