using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace MonitoringApp;

public interface ILogAnalyticsSecretProvider
{
    Task<string> GetClientSecretAsync(CancellationToken cancellationToken);
}

public sealed class KeyVaultLogAnalyticsSecretProvider : ILogAnalyticsSecretProvider
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private readonly CrossTenantLogAnalyticsOptions options;
    private readonly SecretClient? client;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private string? cachedSecret;
    private DateTimeOffset cachedUntil;

    public KeyVaultLogAnalyticsSecretProvider(CrossTenantLogAnalyticsOptions options)
        : this(options, CreateClient(options), TimeProvider.System)
    {
    }

    internal KeyVaultLogAnalyticsSecretProvider(
        CrossTenantLogAnalyticsOptions options, SecretClient? client, TimeProvider clock)
    {
        this.options = options;
        this.client = options.Enabled ? client ?? throw new ArgumentNullException(nameof(client)) : null;
        this.clock = clock;
    }

    private static SecretClient? CreateClient(CrossTenantLogAnalyticsOptions options)
    {
        if (!options.Enabled)
            return null;

        var credential = new ManagedIdentityCredential(
            ManagedIdentityId.FromUserAssignedClientId(options.ManagedIdentityClientId));
        return new SecretClient(new Uri(options.KeyVaultUri), credential);
    }

    public async Task<string> GetClientSecretAsync(CancellationToken cancellationToken)
    {
        if (client is null)
        {
            throw new InvalidOperationException("Cross-tenant Log Analytics is disabled.");
        }
        if (!string.IsNullOrEmpty(cachedSecret) && cachedUntil > clock.GetUtcNow())
        {
            return cachedSecret;
        }

        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrEmpty(cachedSecret) && cachedUntil > clock.GetUtcNow())
            {
                return cachedSecret;
            }

            var response = await client.GetSecretAsync(
                options.ClientSecretName,
                cancellationToken: cancellationToken);
            if (string.IsNullOrEmpty(response.Value.Value))
            {
                throw new InvalidOperationException("The configured Key Vault secret has an empty value.");
            }

            cachedSecret = response.Value.Value;
            cachedUntil = clock.GetUtcNow().Add(CacheDuration);
            return cachedSecret;
        }
        finally
        {
            refreshLock.Release();
        }
    }
}
