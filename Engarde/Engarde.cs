using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Engarde;

/// <summary>Inline feature queries using the same configuration as attribute guards.</summary>
public sealed partial class Engarde : IEngarde
{
    private static readonly ConcurrentDictionary<Type, bool> SkipInlineFailuresCache = new();
    private readonly IServiceProvider? _queryServices;
    private readonly bool _createScopePerCall;

    private Engarde(IServiceProvider? services, bool createScopePerCall)
    {
        _queryServices = services;
        _createScopePerCall = createScopePerCall;
    }

    /// <summary>Creates a query service using the services activated with BeginScope or Activate when called.</summary>
    public static Engarde CreateInstance() => new(null, false);

    /// <summary>
    /// Creates a query service for an existing container. Scoped/transient instances borrow its services;
    /// singleton instances create a temporary DI scope per operation outside an active request evaluation.
    /// </summary>
    public static Engarde CreateInstance(IServiceProvider services, ServiceLifetime lifetime = ServiceLifetime.Scoped)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        ValidateLifetime(lifetime);
        return new(services, lifetime == ServiceLifetime.Singleton);
    }

    internal static void ValidateLifetime(ServiceLifetime lifetime)
    {
        if (lifetime is not (ServiceLifetime.Singleton or ServiceLifetime.Scoped or ServiceLifetime.Transient))
            throw new ArgumentOutOfRangeException(nameof(lifetime));
    }

    /// <summary>
    /// Evaluates a feature with the registered feature store and feature scope id;
    /// </summary>
    public ValueTask<bool> IsFeatureEnabledAsync(string featureFlagName) => IsFeatureEnabledAsync(featureFlagName, default);

    /// <inheritdoc cref="IsFeatureEnabledAsync(string)"/>
    public ValueTask<bool> IsFeatureEnabledAsync(string featureFlagName, CancellationToken cancellationToken) =>
        QueryDefaultAsync(featureFlagName, null, cancellationToken);

    /// <summary>
    /// Evaluates a feature for the given feature scope id instead of resolving one.
    /// <typeparamref name="TFeatureScopeId"/> must match the registered store's feature scope id.
    /// </summary>
    public ValueTask<bool> IsFeatureEnabledAsync<TFeatureScopeId>(
        string featureFlagName, TFeatureScopeId scopeId) =>
        IsFeatureEnabledAsync(featureFlagName, scopeId, default);

    /// <inheritdoc cref="IsFeatureEnabledAsync{TFeatureScopeId}(string, TFeatureScopeId)"/>
    public ValueTask<bool> IsFeatureEnabledAsync<TFeatureScopeId>(
        string featureFlagName, TFeatureScopeId scopeId, CancellationToken cancellationToken) =>
        QueryDefaultAsync(featureFlagName, new ScopeIdOverride(scopeId), cancellationToken);

    /// <summary>
    /// Evaluates the feature names encapsulated by a guard using its configured store and scope.
    /// Returns true only when all its features are enabled.
    /// </summary>
    public ValueTask<bool> IsFeatureEnabledAsync(EngardeAttribute guard) => IsFeatureEnabledAsync(guard, default(CancellationToken));

    /// <inheritdoc cref="IsFeatureEnabledAsync(EngardeAttribute)"/>
    public ValueTask<bool> IsFeatureEnabledAsync(EngardeAttribute guard, CancellationToken cancellationToken) =>
        QueryAsync(guard, null, null, cancellationToken);

    /// <summary>Evaluates a feature with a custom guard; see <see cref="IsFeatureEnabledAsync(EngardeAttribute)"/>.</summary>
    public ValueTask<bool> IsFeatureEnabledAsync(EngardeAttribute guard, string featureFlagName) =>
        IsFeatureEnabledAsync(guard, featureFlagName, default(CancellationToken));

    /// <inheritdoc cref="IsFeatureEnabledAsync(EngardeAttribute, string)"/>
    public ValueTask<bool> IsFeatureEnabledAsync(EngardeAttribute guard, string featureFlagName, CancellationToken cancellationToken)
    {
        ValidateFeatureName(featureFlagName, nameof(featureFlagName));
        return QueryAsync(guard, featureFlagName, null, cancellationToken);
    }

    /// <summary>
    /// Evaluates the guard's features for the given feature scope id instead of resolving one.
    /// <typeparamref name="TFeatureScopeId"/> must match the guard's feature scope id, or the registered store's.
    /// </summary>
    public ValueTask<bool> IsFeatureEnabledAsync<TFeatureScopeId>(EngardeAttribute guard, TFeatureScopeId scopeId) =>
        IsFeatureEnabledAsync(guard, scopeId, default);

    /// <inheritdoc cref="IsFeatureEnabledAsync{TFeatureScopeId}(EngardeAttribute, TFeatureScopeId)"/>
    public ValueTask<bool> IsFeatureEnabledAsync<TFeatureScopeId>(
        EngardeAttribute guard, TFeatureScopeId scopeId, CancellationToken cancellationToken) =>
        QueryAsync(guard, null, new ScopeIdOverride(scopeId), cancellationToken);

    /// <summary>Evaluates a feature with a custom guard for the given feature scope id instead of resolving one.</summary>
    public ValueTask<bool> IsFeatureEnabledAsync<TFeatureScopeId>(EngardeAttribute guard, string featureFlagName, TFeatureScopeId scopeId) =>
        IsFeatureEnabledAsync(guard, featureFlagName, scopeId, default);

    /// <inheritdoc cref="IsFeatureEnabledAsync{TFeatureScopeId}(EngardeAttribute, string, TFeatureScopeId)"/>
    public ValueTask<bool> IsFeatureEnabledAsync<TFeatureScopeId>(
        EngardeAttribute guard, string featureFlagName, TFeatureScopeId scopeId, CancellationToken cancellationToken)
    {
        ValidateFeatureName(featureFlagName, nameof(featureFlagName));
        return QueryAsync(guard, featureFlagName, new ScopeIdOverride(scopeId), cancellationToken);
    }

    private async ValueTask<bool> QueryAsync(
        EngardeAttribute guard, string? featureFlagName, ScopeIdOverride? scopeId, CancellationToken cancellationToken)
    {
        if (guard is null) throw new ArgumentNullException(nameof(guard));
        var guardType = guard.GetType();
        var featureNames = featureFlagName is null ? guard.FeatureNames : [featureFlagName];
        await using var evaluation = EngardeEvaluation.Begin(_queryServices, _createScopePerCall, cancellationToken);
        var enabled = true;
        EngardeContext? context = null;
        foreach (var featureName in featureNames)
        {
            ValidateFeatureName(featureName, nameof(guard));
            if (await guard.EvaluateAsync(evaluation, featureName, guardType, null, scopeId).ConfigureAwait(false))
                continue;
            enabled = false;
            if (evaluation.SkipInlineFailures || SkipsInlineFailures(guardType))
                continue;
            context ??= evaluation.CreateContext(guardType, null, scopeId);
            guard.OnFeatureMissing(featureName, context);
        }
        return enabled;
    }

    private async ValueTask<bool> QueryDefaultAsync(
        string featureFlagName, ScopeIdOverride? scopeId, CancellationToken cancellationToken)
    {
        ValidateFeatureName(featureFlagName, nameof(featureFlagName));
        await using var evaluation = EngardeEvaluation.Begin(_queryServices, _createScopePerCall, cancellationToken);
        var enabled = await evaluation.DefaultAsync(featureFlagName, scopeId).ConfigureAwait(false);
        if (!enabled && !evaluation.SkipInlineFailures)
            evaluation.CreateContext(typeof(Engarde), null, scopeId).HandleFeatureMissing(featureFlagName);
        return enabled;
    }

    private static bool SkipsInlineFailures(Type guardType) => SkipInlineFailuresCache.GetOrAdd(guardType,
        static type => type.IsDefined(typeof(SkipInlineFailuresAttribute), inherit: true));

    private static void ValidateFeatureName(string featureName, string parameterName)
    {
        if (featureName is null)
            throw new ArgumentNullException(parameterName);
        if (string.IsNullOrWhiteSpace(featureName))
            throw new ArgumentException("The feature flag name cannot be null, empty, or whitespace.", parameterName);
    }
}
