using Microsoft.Extensions.DependencyInjection;

namespace Harness.Triggers;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the trigger queue and engine. The host decides whether the engine runs as a hosted service.</summary>
    public static IServiceCollection AddHarnessTriggers(this IServiceCollection services)
    {
        services.AddSingleton<TriggerQueue>();
        services.AddSingleton<TriggerEngine>();
        return services;
    }
}
