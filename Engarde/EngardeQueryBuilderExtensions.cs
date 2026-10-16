using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Engarde;

/// <summary>Registration of instance query interfaces in the application's DI container.</summary>
public static class EngardeQueryBuilderExtensions
{
    /// <summary>Sets the default lifetime for IEngarde and registered generated query interfaces. Defaults to scoped.</summary>
    public static EngardeBuilder UseQueryLifetime(this EngardeBuilder builder, ServiceLifetime lifetime)
    {
        if (builder is null) throw new ArgumentNullException(nameof(builder));
        Engarde.ValidateLifetime(lifetime);
        builder.QueryLifetime = lifetime;
        return builder;
    }

    internal static void RegisterQueries(this EngardeBuilder builder, IServiceCollection services)
    {
        var lifetime = builder.QueryLifetime;
        services.TryAdd(new ServiceDescriptor(typeof(Engarde), provider => Engarde.CreateInstance(provider, lifetime), lifetime));
        services.TryAdd(new ServiceDescriptor(typeof(IEngarde), provider => provider.GetRequiredService<Engarde>(), lifetime));
        EngardeGeneratedQueryRegistration.Register(services, lifetime);
    }
}

/// <summary>Compile-time registration hook used by generated guard query implementations.</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public static class EngardeGeneratedQueryRegistration
{
    private static readonly object Sync = new();
    private static readonly List<(Type Service, Type Implementation)> Registrations = [];

    public static void Add(Type service, Type implementation)
    {
        if (service is null) throw new ArgumentNullException(nameof(service));
        if (implementation is null) throw new ArgumentNullException(nameof(implementation));
        lock (Sync)
        {
            if (!Registrations.Contains((service, implementation)))
                Registrations.Add((service, implementation));
        }
    }

    internal static void Register(IServiceCollection services, ServiceLifetime lifetime)
    {
        (Type Service, Type Implementation)[] registrations;
        lock (Sync) registrations = [.. Registrations];
        foreach (var registration in registrations)
            services.TryAdd(new ServiceDescriptor(registration.Service, registration.Implementation, lifetime));
    }
}
