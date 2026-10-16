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
    /// Activates guards for the current async flow only until disposed (e.g. in tests).
    /// A Microsoft DI root provider creates an owned child scope; an existing scoped provider is borrowed.
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

    /// <summary>
    /// Reuses scoped services, resolved scope IDs and feature results across inline and woven checks
    /// in the current async flow until disposed. Start a new scope for a fresh status snapshot.
    /// </summary>
    public static IDisposable BeginEvaluationScope() => EngardeEvaluation.Begin();

    /// <summary>
    /// Reuses an existing DI scope's services for an evaluation session. The caller owns the services;
    /// disposing this session ends its cache without disposing the supplied service provider.
    /// </summary>
    public static IDisposable BeginEvaluationScope(IServiceProvider services)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        return EngardeEvaluation.Begin(services);
    }

    internal static (IServiceProvider Services, EngardeConfiguration Configuration) Current
    {
        get
        {
            return TryGetCurrent(out var current)
                ? (current.Services, current.Configuration)
                : throw new InvalidOperationException("Feature guards are not active, register them with AddEngarde");
        }
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

    internal static bool TryGetGlobalServices(out IServiceProvider services)
    {
        services = _services!;
        return services is not null;
    }

    internal readonly record struct ActiveServices(
        IServiceProvider Services, EngardeConfiguration Configuration, bool CreateScope);

    private sealed record ActiveScope(IServiceProvider Services, bool CreateScope);

    private sealed class Scope(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
