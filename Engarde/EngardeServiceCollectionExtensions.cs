using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Engarde;

public static class EngardeServiceCollectionExtensions
{
    /// <summary>
    /// Registers how [Engarde] features are evaluated and how disabled ones are handled.
    /// Guards become active when the host starts (or on <see cref="global::Engarde.Engarde.Activate(IServiceProvider)"/> without a host).
    /// Register services that use class/interface fallback types before calling this method so their descriptors can be wrapped.
    /// </summary>
    public static IServiceCollection AddEngarde(this IServiceCollection services, Action<EngardeBuilder> configure)
    {
        var builder = new EngardeBuilder();
        configure(builder);

        services.AddSingleton(builder.Build());
        RegisterTypeFallbacks(services);
        builder.RegisterQueries(services);
        return services.AddHostedService<EngardeActivator>();
    }

    private static void RegisterTypeFallbacks(IServiceCollection services)
    {
        var descriptors = services.ToArray();
        foreach (var descriptor in descriptors)
        {
            if (descriptor.IsKeyedService || descriptor.ServiceType.IsGenericTypeDefinition)
                continue;
            var guardedType = descriptor.ServiceType.GetCustomAttributes(typeof(EngardeAttribute), false)
                .Cast<EngardeAttribute>().FirstOrDefault(HasTypeFallback)
                ?? descriptor.ImplementationType?.GetCustomAttributes(typeof(EngardeAttribute), false)
                    .Cast<EngardeAttribute>().FirstOrDefault(HasTypeFallback);
            if (guardedType is null)
                continue;

            var fallbackType = guardedType.FallbackType!;
            services.TryAdd(new ServiceDescriptor(fallbackType, fallbackType, descriptor.Lifetime));
            var index = services.IndexOf(descriptor);
            if (index < 0) continue;
            services[index] = new ServiceDescriptor(descriptor.ServiceType, provider =>
                EngardeChecks.ResolveGuardedService(provider, guardedType,
                    () => CreateService(descriptor, provider)), descriptor.Lifetime);
        }
    }

    private static bool HasTypeFallback(EngardeAttribute guard) =>
        guard.FallbackType is not null && guard.FallbackMethod is null;

    private static object CreateService(ServiceDescriptor descriptor, IServiceProvider provider)
    {
        if (descriptor.ImplementationInstance is not null)
            return descriptor.ImplementationInstance;
        if (descriptor.ImplementationFactory is not null)
            return descriptor.ImplementationFactory(provider)
                ?? throw new InvalidOperationException($"Factory for '{descriptor.ServiceType}' returned null.");
        return ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
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
