using Microsoft.Extensions.DependencyInjection;

namespace Engarde;

/// <summary>Inline feature queries using the same configuration as attribute guards.</summary>
public sealed partial class Engarde : IEngarde
{
    private readonly IServiceProvider? _queryServices;
    private readonly bool _createScopePerCall;

    private Engarde(IServiceProvider? services, bool createScopePerCall)
    {
        _queryServices = services;
        _createScopePerCall = createScopePerCall;
    }

    /// <summary>Creates a query service using the active application or evaluation scope when called.</summary>
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

    public async ValueTask<bool> IsEnabledAsync(string featureFlagName)
    {
        ValidateFeatureName(featureFlagName, nameof(featureFlagName));
        return await EngardeEvaluation.QueryAsync(featureFlagName, _queryServices, _createScopePerCall).ConfigureAwait(false);
    }

    /// <summary>
    /// Evaluates the feature names encapsulated by a guard using its configured store and scope.
    /// Returns true only when all its features are enabled; does not invoke the missing-feature handler.
    /// </summary>
    public async ValueTask<bool> IsEnabledAsync(EngardeAttribute guard)
    {
        if (guard is null) throw new ArgumentNullException(nameof(guard));
        await using var evaluation = EngardeEvaluation.Acquire(_queryServices, _createScopePerCall);
        var enabled = true;
        for (var index = 0; index < guard.FeatureNames.Count; index++)
        {
            var featureName = guard.FeatureNames[index];
            ValidateFeatureName(featureName, nameof(guard));
            enabled &= await guard.EvaluateAsync(evaluation.State, featureName, guard.GetType(), null).ConfigureAwait(false);
        }
        return enabled;
    }

    /// <summary>Evaluates a custom guard without invoking its missing-feature handler.</summary>
    public async ValueTask<bool> IsEnabledAsync(EngardeAttribute guard, string featureFlagName)
    {
        if (guard is null) throw new ArgumentNullException(nameof(guard));
        ValidateFeatureName(featureFlagName, nameof(featureFlagName));
        await using var evaluation = EngardeEvaluation.Acquire(_queryServices, _createScopePerCall);
        return await guard.EvaluateAsync(evaluation.State, featureFlagName, guard.GetType(), null).ConfigureAwait(false);
    }

    private static void ValidateFeatureName(string featureName, string parameterName)
    {
        if (featureName is null)
            throw new ArgumentNullException(parameterName);
        if (string.IsNullOrWhiteSpace(featureName))
            throw new ArgumentException("The feature flag name cannot be null, empty, or whitespace.", parameterName);
    }
}
