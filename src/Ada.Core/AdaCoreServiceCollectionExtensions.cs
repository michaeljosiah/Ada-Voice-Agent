using Microsoft.Extensions.DependencyInjection;

namespace Ada.Core;

public static class AdaCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers Ada's core services. M0 wires the <see cref="EchoEngine"/>; later milestones
    /// replace the engine registration with the real agent while keeping every host unchanged.
    /// </summary>
    public static IServiceCollection AddAdaCore(this IServiceCollection services)
    {
        services.AddSingleton<IAdaEngine, EchoEngine>();
        return services;
    }
}
