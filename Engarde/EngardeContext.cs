using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Engarde;

public sealed class EngardeContext
{
    private readonly EngardeConfiguration? _configuration;
    private readonly EngardeEvaluation _evaluation;

    internal EngardeContext(EngardeConfiguration? configuration, IServiceProvider services, MemberInfo member, object? value, EngardeEvaluation evaluation)
    {
        _configuration = configuration;
        _evaluation = evaluation;
        Services = services;
        Member = member;
        Value = value;
    }

    /// <summary>
    /// Scoped services for this evaluation. Owned evaluation services end with the evaluation;
    /// borrowed request services remain owned by the HTTP request. A self-contained guard running without
    /// <c>AddEngarde</c> receives an empty provider.
    /// </summary>
    public IServiceProvider Services { get; }

    /// <summary>
    /// The guarded method, constructor, property, field or method of a guarded parameter; the guard type for inline queries
    /// </summary>
    public MemberInfo Member { get; }

    /// <summary>
    /// The incoming value for a property/field assignment or guarded parameter.
    /// Null for method calls (including lambdas), constructor guards and inline queries.
    /// </summary>
    public object? Value { get; }

    internal T GetRequiredService<T>() where T : notnull => Services.GetRequiredService<T>();

    internal async ValueTask<bool> IsEnabledByDefaultAsync(string featureName, EngardeAttribute guard)
    {
        var store = RequiredConfiguration.FeatureStore;
        var scopeId = store.ScopeIdType is null ? null : await GetFeatureScopeIdAsync(guard, store);

        return await _evaluation.CheckStoreAsync(store, featureName, scopeId).ConfigureAwait(false);
    }

    internal void HandleFeatureMissing(string featureName)
    {
        RequiredConfiguration.OnFeatureMissing(featureName, this);
    }

    private async ValueTask<object?> GetFeatureScopeIdAsync(EngardeAttribute guard, FeatureStoreRegistration store)
    {
        if (guard.GetFeatureScopeId(this) is not { } custom)
        {
            return await _evaluation.ScopeIdAsync(new EngardeEvaluation.ScopeKey(store), (store, Services),
                static state => state.store.ScopeIdFactory!(state.Services)).ConfigureAwait(false);
        }

        if (custom.Type != store.ScopeIdType)
        {
            throw new InvalidOperationException(
                $"{guard.GetType().Name} provides a {custom.Type.Name} feature scope id, but the feature store expects {store.ScopeIdType!.Name}");
        }

        return await GetOrAddFeatureScopeIdAsync(guard, custom.Resolve);
    }

    /// <summary>
    /// Resolves an attribute's own feature scope id once per evaluation scope and cache key
    /// </summary>
    internal async ValueTask<object?> GetOrAddFeatureScopeIdAsync(EngardeAttribute guard, Func<ValueTask<object?>> resolve)
    {
        return await _evaluation.ScopeIdAsync(new EngardeEvaluation.ScopeKey(guard.GetType(), guard.EvaluationCacheKey),
            resolve, static action => action()).ConfigureAwait(false);
    }

    private EngardeConfiguration RequiredConfiguration => _configuration
        ?? throw new InvalidOperationException(
            "This guard uses the default feature store or missing-feature handler. Register it with AddEngarde, or override both methods in the guard.");
}
