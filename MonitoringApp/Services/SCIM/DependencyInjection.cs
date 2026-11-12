using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MonitoringApp;

public static class ScimServiceCollectionExtensions
{
    public static IServiceCollection AddScimServices(this IServiceCollection services, ScimOptions options,
        IdentityDirectoryOptions directory)
    {
        var errors = options.Validate().ToList();
        if (options.Enabled && !directory.Enabled)
            errors.Add("IdentityDirectory.Enabled must be true before SCIM is enabled.");
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors));
        services.AddSingleton(options);
        services.TryAddScoped<IScimUserService, ScimUserService>();
        services.TryAddScoped<IScimGroupService, ScimGroupService>();
        return services;
    }
}
