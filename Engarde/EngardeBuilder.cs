using Microsoft.Extensions.DependencyInjection;

namespace Engarde;

public sealed class EngardeBuilder
{
    private FeatureStoreRegistration? _featureStore;
    private Action<string, EngardeContext>? _onFeatureMissing;
    internal ServiceLifetime QueryLifetime { get; set; } = ServiceLifetime.Scoped;

    /// <summary>Default: false. When true, inline queries return false without calling <c>OnFeatureMissing</c>.</summary>
    public bool SkipInlineFailures { get; set; }

    /// <summary>Default: false. When true, fallbacks dispatch without calling <c>OnFeatureMissing</c> first.</summary>
    public bool SkipFallbackFailures { get; set; }

    /// <summary>
    /// Evaluates features without a feature scope
    /// </summary>
    public EngardeBuilder UseFeatureStore<TFeatureStore>(Func<TFeatureStore, string, ValueTask<bool>> isEnabledAsync)
        where TFeatureStore : notnull
    {
        _featureStore = new FeatureStoreRegistration(
            null,
            null,
            (services, featureName, _) => isEnabledAsync(services.GetRequiredService<TFeatureStore>(), featureName));

        return this;
    }

    /// <summary>
    /// Evaluates features for a feature scope id taken from a scoped <typeparamref name="TFeatureScopeIdProvider"/>
    /// </summary>
    public EngardeBuilder UseFeatureStore<TFeatureStore, TFeatureScopeIdProvider, TFeatureScopeId>(
        Func<TFeatureStore, string, TFeatureScopeId, ValueTask<bool>> isEnabledAsync,
        Func<TFeatureScopeIdProvider, ValueTask<TFeatureScopeId>> getScopeIdAsync)
        where TFeatureStore : notnull
        where TFeatureScopeIdProvider : notnull
    {
        _featureStore = new FeatureStoreRegistration(
            typeof(TFeatureScopeId),
            async services => await getScopeIdAsync(services.GetRequiredService<TFeatureScopeIdProvider>()),
            (services, featureName, scopeId) =>
                isEnabledAsync(services.GetRequiredService<TFeatureStore>(), featureName, (TFeatureScopeId)scopeId!));

        return this;
    }

    /// <summary>
    /// Called for each disabled feature. Throw to block a guarded call; returning lets the call proceed.
    /// Disabled property and field writes are skipped after the handler returns. Optional; without a handler
    /// nothing happens for a disabled feature.
    /// </summary>
    public EngardeBuilder OnFeatureMissing(Action<string, EngardeContext> onFeatureMissing)
    {
        _onFeatureMissing = onFeatureMissing;

        return this;
    }

    internal EngardeConfiguration Build()
    {
        return new EngardeConfiguration(
            _featureStore ?? throw new InvalidOperationException(
                $"Feature guards need a feature store, call {nameof(UseFeatureStore)}"),
            _onFeatureMissing ?? (static (_, _) => { }),
            SkipInlineFailures,
            SkipFallbackFailures);
    }
}

internal sealed record FeatureStoreRegistration(
    Type? ScopeIdType,
    Func<IServiceProvider, ValueTask<object?>>? GetScopeIdAsync,
    Func<IServiceProvider, string, object?, ValueTask<bool>> IsEnabledAsync);

internal sealed record EngardeConfiguration(
    FeatureStoreRegistration FeatureStore,
    Action<string, EngardeContext> OnFeatureMissing,
    bool SkipInlineFailures,
    bool SkipFallbackFailures);
