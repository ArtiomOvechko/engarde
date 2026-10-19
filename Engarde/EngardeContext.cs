using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Engarde;

public sealed class EngardeContext
{
    private readonly EngardeConfiguration? _configuration;
    private readonly EngardeEvaluation _evaluation;

    internal EngardeContext(EngardeConfiguration? configuration, IServiceProvider? services, MemberInfo member, object? value,
        EngardeEvaluation evaluation, ScopeIdOverride? scopeIdOverride = null)
    {
        _configuration = configuration;
        _evaluation = evaluation;
        ScopeIdOverride = scopeIdOverride;
        OptionalServices = services;
        Member = member;
        Value = value;
    }

    /// <summary>
    /// Scoped services for this check. A child scope created for the check ends with it; borrowed request services
    /// remain owned by the HTTP request. Throws when guards run without <c>AddEngarde</c>.
    /// </summary>
    public IServiceProvider Services => OptionalServices ?? throw NoServices();

    /// <summary>The services for this check, or null when guards run without <c>AddEngarde</c></summary>
    internal IServiceProvider? OptionalServices { get; }

    internal const string TestingHint =
        "If you encounter this error in your unit tests, please use En-Garde.Testing (read more here: " +
        "https://www.nuget.org/packages/En-Garde#:~:text=ServiceLifetime.Singleton).-,Testing,-dotnet%20add%20package)";

    internal static InvalidOperationException NoServices() =>
        new("Feature guards have no services, register them with AddEngarde");

    /// <summary>
    /// The guarded method, constructor, property, field or method of a guarded parameter; the guard type for inline queries
    /// </summary>
    public MemberInfo Member { get; }

    /// <summary>
    /// The incoming value for a property/field assignment or guarded parameter.
    /// Null for method calls (including lambdas), constructor guards and inline queries.
    /// </summary>
    public object? Value { get; }

    /// <summary>The feature scope id passed to an inline query, used instead of resolving one</summary>
    internal ScopeIdOverride? ScopeIdOverride { get; }

    internal CancellationToken CancellationToken => _evaluation.CancellationToken;

    internal T GetRequiredService<T>() where T : notnull => Services.GetRequiredService<T>();

    internal async ValueTask<bool> IsEnabledByDefaultAsync(string featureName, EngardeAttribute guard)
    {
        var store = RequiredConfiguration.FeatureStore;
        if (ScopeIdOverride is not null && store.ScopeIdType is null)
            throw new InvalidOperationException("The registered feature store evaluates features without a feature scope id");
        var scopeId = store.ScopeIdType is null ? null : await GetScopeIdAsync(guard, store);

        return await _evaluation.CheckStoreAsync(store, featureName, scopeId).ConfigureAwait(false);
    }

    internal void HandleFeatureMissing(string featureName)
    {
        RequiredConfiguration.OnFeatureMissing(featureName, this);
    }

    private async ValueTask<object?> GetScopeIdAsync(EngardeAttribute guard, FeatureStoreRegistration store)
    {
        if (guard.GetFeatureScopeId(this) is not { } custom)
        {
            if (ScopeIdOverride is not null)
                return ScopeIdOverride.Get(store.ScopeIdType!, guard.GetType());
            return await store.GetScopeIdAsync!(Services).ConfigureAwait(false);
        }

        if (custom.Type != store.ScopeIdType)
        {
            throw new InvalidOperationException(
                $"{guard.GetType().Name} provides a {custom.Type.Name} feature scope id, but the feature store expects {store.ScopeIdType!.Name}");
        }
        if (ScopeIdOverride is not null)
            return ScopeIdOverride.Get(custom.Type, guard.GetType());

        return await custom.Resolve();
    }

    private EngardeConfiguration RequiredConfiguration => _configuration
        ?? throw new InvalidOperationException(
            "This guard uses the default feature store or missing-feature handler. Register it with AddEngarde, or override both methods in the guard. "
            + TestingHint);
}
