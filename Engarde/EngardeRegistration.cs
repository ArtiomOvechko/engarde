using Microsoft.Extensions.DependencyInjection;

namespace Engarde;

public sealed partial class Engarde
{
    private static readonly AsyncLocal<ActiveScope?> ScopedServices = new();
    private static IServiceProvider? _services;

    /// <summary>
    /// Activates guards for the whole application with the provider where <c>AddEngarde</c> was registered;
    /// done automatically when the host starts
    /// </summary>
    public static void Activate(IServiceProvider services)
    {
        _services = services;
    }

    /// <summary>
    /// Activates guards for the current async flow only until disposed (e.g. in tests or per request).
    /// A Microsoft DI root provider gets a child scope per check; an existing scoped provider is borrowed.
    /// </summary>
    public static IDisposable BeginScope(IServiceProvider services)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        var createScope = services.GetType().FullName == "Microsoft.Extensions.DependencyInjection.ServiceProvider"
            || ReferenceEquals(services, services.GetService<IServiceScopeFactory>());
        return BeginScope(services, createScope);
    }

    /// <summary>
    /// Activates guards for the current async flow and explicitly selects whether evaluations create an owned
    /// child scope or borrow the supplied provider.
    /// </summary>
    public static IDisposable BeginScope(IServiceProvider services, bool createScope)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        var previous = ScopedServices.Value;
        ScopedServices.Value = new(services, createScope);

        return new Scope(() => ScopedServices.Value = previous);
    }

    internal static bool TryGetCurrent(out ActiveServices current)
    {
        if (ScopedServices.Value is { } scoped)
        {
            current = new(scoped.Services, scoped.Services.GetRequiredService<EngardeConfiguration>(), scoped.CreateScope);
            return true;
        }
        if (_services is null)
        {
            current = default;
            return false;
        }

        current = new(_services, _services.GetRequiredService<EngardeConfiguration>(), true);
        return true;
    }

    internal static bool TryGetScopedServices(out ActiveServices services)
    {
        if (ScopedServices.Value is not { } scoped)
        {
            services = default;
            return false;
        }

        services = new(scoped.Services, scoped.Services.GetRequiredService<EngardeConfiguration>(), scoped.CreateScope);
        return true;
    }

    internal readonly record struct ActiveServices(
        IServiceProvider Services, EngardeConfiguration Configuration, bool CreateScope);

    private sealed record ActiveScope(IServiceProvider Services, bool CreateScope);

    private sealed class Scope(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
