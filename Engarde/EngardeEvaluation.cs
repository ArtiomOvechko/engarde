using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Engarde;

/// <summary>
/// The services one check runs with. A root provider gets a child scope that ends with the check.
/// Without <c>AddEngarde</c> there are no services, which self-contained guards do not need.
/// </summary>
internal sealed class EngardeEvaluation : IAsyncDisposable
{
    private readonly AsyncServiceScope? _ownedScope;
    private readonly EngardeConfiguration? _configuration;
    internal IServiceProvider? Services { get; }
    internal CancellationToken CancellationToken { get; }

    private EngardeEvaluation(IServiceProvider? services, EngardeConfiguration? configuration, bool createScope,
        CancellationToken cancellationToken)
    {
        _configuration = configuration;
        CancellationToken = cancellationToken;
        if (services is not null && createScope)
        {
            _ownedScope = services.CreateAsyncScope();
            Services = _ownedScope.Value.ServiceProvider;
        }
        else
            Services = services;
    }

    /// <summary>
    /// Uses the query's own provider when it has one, otherwise the provider activated with <c>BeginScope</c>
    /// or <c>Activate</c>. Singleton queries create a scope per check, unless the active scope is from their container.
    /// </summary>
    internal static EngardeEvaluation Begin(
        IServiceProvider? services = null, bool createScope = false, CancellationToken cancellationToken = default)
    {
        if (services is not null)
        {
            var configuration = services.GetRequiredService<EngardeConfiguration>();
            return createScope && global::Engarde.Engarde.TryGetScopedServices(out var scoped)
                && ReferenceEquals(scoped.Configuration, configuration)
                    ? new(scoped.Services, configuration, scoped.CreateScope, cancellationToken)
                    : new(services, configuration, createScope, cancellationToken);
        }

        return global::Engarde.Engarde.TryGetCurrent(out var current)
            ? new(current.Services, current.Configuration, current.CreateScope, cancellationToken)
            : new(null, null, false, cancellationToken);
    }

    internal EngardeContext CreateContext(MemberInfo member, object? value, ScopeIdOverride? scopeId = null) =>
        new(_configuration, Services, member, value, this, scopeId);

    internal async ValueTask<bool> DefaultAsync(string featureName, ScopeIdOverride? scopeIdOverride = null)
    {
        CancellationToken.ThrowIfCancellationRequested();
        var store = RequiredConfiguration.FeatureStore;
        if (scopeIdOverride is not null && store.ScopeIdType is null)
            throw new InvalidOperationException("The registered feature store evaluates features without a feature scope id");
        var scopeId = store.ScopeIdType is null ? null
            : scopeIdOverride is not null ? scopeIdOverride.Get(store.ScopeIdType, typeof(EngardeAttribute))
            : await store.GetScopeIdAsync!(Services!).ConfigureAwait(false);
        return await CheckStoreAsync(store, featureName, scopeId).ConfigureAwait(false);
    }

    // A configuration only comes from a provider, so Services is set whenever the registered store is used.
    internal ValueTask<bool> CheckStoreAsync(FeatureStoreRegistration store, string featureName, object? scopeId) =>
        store.IsEnabledAsync(Services!, featureName, scopeId);

    internal ValueTask<bool> CheckGuardAsync(
        EngardeAttribute guard, string featureName, MemberInfo member, object? value, ScopeIdOverride? scopeId)
    {
        CancellationToken.ThrowIfCancellationRequested();
        return guard.IsEnabledAsync(featureName, CreateContext(member, value, scopeId), CancellationToken);
    }

    public ValueTask DisposeAsync() => _ownedScope?.DisposeAsync() ?? default;

    private EngardeConfiguration RequiredConfiguration => _configuration
        ?? throw new InvalidOperationException(
            "This guard uses the default feature store. Register it with AddEngarde, or override IsEnabledAsync and OnFeatureMissing in the guard. "
            + EngardeContext.TestingHint);
}
