using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Engarde;

public static class EngardeServiceCollectionExtensions
{
    /// <summary>
    /// Registers how [Engarde] features are evaluated and how disabled ones are handled.
    /// Guards become active when the host starts (or on <see cref="global::Engarde.Engarde.Activate(IServiceProvider)"/> without a host).
    /// </summary>
    public static IServiceCollection AddEngarde(this IServiceCollection services, Action<EngardeBuilder> configure)
    {
        var builder = new EngardeBuilder();
        configure(builder);

        services.AddSingleton(builder.Build());
        builder.RegisterQueries(services);
        return services.AddHostedService<EngardeActivator>();
    }

    private sealed class EngardeActivator(IServiceProvider services) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            global::Engarde.Engarde.Activate(services);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
