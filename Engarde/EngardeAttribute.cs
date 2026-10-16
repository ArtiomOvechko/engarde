using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace Engarde;

/// <summary>
/// Validates features when the guarded member is used. Woven into the code at build time, so it applies
/// to every call or assignment in a woven assembly, including a class calling its own members:
/// <list type="bullet">
/// <item>Method / constructor: on each call.</item>
/// <item>Class / struct / record: on every emitted instance/static constructor and ordinary method call,
/// including implicit class constructors (property accessors and other compiler-generated methods are skipped).</item>
/// <item>Property: when the setter (or init) is given a non-empty value.</item>
/// <item>Field: when a non-empty value is assigned.</item>
/// <item>Parameter: when the method is called with a non-empty argument.</item>
/// </list>
/// A value is empty when it is null, default, an empty string or an empty collection.
/// Features are evaluated and failures handled as registered with <c>AddEngarde</c>. To replace that for specific members, derive from:
/// <list type="bullet">
/// <item><see cref="EngardeAttribute{TFeatureScopeIdProvider, TFeatureScopeId}"/>: own feature scope id, registered feature store.</item>
/// <item><see cref="EngardeAttribute{TFeatureStore}"/>: own feature store without a feature scope.</item>
/// <item><see cref="EngardeAttribute{TFeatureStore, TFeatureScopeIdProvider, TFeatureScopeId}"/>: own feature store and feature scope id.</item>
/// <item>This attribute, overriding <see cref="IsEnabledAsync"/> and/or <see cref="OnFeatureMissing"/> directly.</item>
/// </list>
/// A guard that overrides both methods and does not resolve services from <see cref="EngardeContext.Services"/>
/// is self-contained and can run without registering <c>AddEngarde</c>.
/// </summary>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method | AttributeTargets.Constructor
        | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter,
    AllowMultiple = true,
    Inherited = false)]
public class EngardeAttribute(params string[] featureNames) : Attribute
{
    private static readonly ConditionalWeakTable<EngardeAttribute, CachedKey> CacheKeys = new();
    private sealed class CachedKey(object value) { internal object Value { get; } = value; }
    public virtual IReadOnlyList<string> FeatureNames { get; } = featureNames;

    /// <summary>
    /// Whether the feature is enabled. Defaults to the registered feature store (with this attribute's feature scope id).
    /// </summary>
    protected internal virtual ValueTask<bool> IsEnabledAsync(string featureName, EngardeContext context)
    {
        return context.IsEnabledByDefaultAsync(featureName, this);
    }

    /// <summary>
    /// Called for each disabled feature. Throwing blocks a method or parameter guard; returning lets that call proceed.
    /// A disabled property or field write is skipped after this method returns. Defaults to the registered handler.
    /// </summary>
    protected internal virtual void OnFeatureMissing(string featureName, EngardeContext context)
    {
        context.HandleFeatureMissing(featureName);
    }

    /// <summary>
    /// Feature scope id passed to the registered feature store; null means the registered feature scope id factory
    /// </summary>
    private protected virtual ScopeId? ResolveFeatureScopeId(EngardeContext context)
    {
        return null;
    }

    /// <summary>
    /// Identity for reusing this guard's store results and scope IDs inside an evaluation scope. Custom guards may
    /// override with an immutable value containing all constructor settings, e.g. (GetType(), variant).
    /// Arbitrary overrides that inspect <see cref="EngardeContext"/> are evaluated for every operation.
    /// The value is memoized on first use for each attribute instance.
    /// </summary>
    protected virtual object CacheKey => GetType() == typeof(EngardeAttribute) ? GetType() : this;

    internal object EvaluationCacheKey => CacheKeys.GetValue(this, static guard => new CachedKey(guard.CacheKey)).Value;

    internal virtual ValueTask<bool> EvaluateAsync(EngardeEvaluation evaluation, string featureName, MemberInfo member, object? value) =>
        GetType() == typeof(EngardeAttribute) ? evaluation.DefaultAsync(featureName)
            : evaluation.CheckGuardAsync(this, featureName, member, value);

    internal ScopeId? GetFeatureScopeId(EngardeContext context) => ResolveFeatureScopeId(context);
}

/// <summary>
/// Evaluates features with the registered feature store, but for its own feature scope id.
/// <typeparamref name="TFeatureScopeId"/> must match the registered feature store.
/// </summary>
public abstract class EngardeAttribute<TFeatureScopeIdProvider, TFeatureScopeId>(params string[] featureNames)
    : EngardeAttribute(featureNames)
    where TFeatureScopeIdProvider : notnull
{
    protected abstract ValueTask<TFeatureScopeId> GetFeatureScopeIdAsync(TFeatureScopeIdProvider provider);

    private protected sealed override ScopeId ResolveFeatureScopeId(EngardeContext context)
    {
        return new ScopeId(
            typeof(TFeatureScopeId),
            async () => await GetFeatureScopeIdAsync(context.GetRequiredService<TFeatureScopeIdProvider>()));
    }
}

/// <summary>
/// Evaluates features with its own feature store, without a feature scope; the registered feature store is not used
/// </summary>
public abstract class EngardeAttribute<TFeatureStore>(params string[] featureNames) : EngardeAttribute(featureNames)
    where TFeatureStore : notnull
{
    internal override ValueTask<bool> EvaluateAsync(EngardeEvaluation evaluation, string featureName, MemberInfo member, object? value) =>
        evaluation.CheckAsync(new EngardeEvaluation.CheckKey(GetType(), featureName, null, EvaluationCacheKey),
            (Guard: this, evaluation.Services, featureName),
            static state => state.Guard.IsEnabledAsync(state.Guard.ResolveFeatureStore(state.Services), state.featureName));

    /// <summary>
    /// Resolves this guard's feature store. The default uses the current DI scope; override to return another
    /// implementation or a fixed instance.
    /// </summary>
    protected virtual TFeatureStore ResolveFeatureStore(IServiceProvider services) =>
        services.GetRequiredService<TFeatureStore>();

    protected abstract ValueTask<bool> IsEnabledAsync(TFeatureStore store, string featureName);

    protected internal sealed override ValueTask<bool> IsEnabledAsync(string featureName, EngardeContext context)
    {
        return IsEnabledAsync(ResolveFeatureStore(context.Services), featureName);
    }
}

/// <summary>
/// Evaluates features with its own feature store and feature scope id; the registered feature store is not used
/// </summary>
public abstract class EngardeAttribute<TFeatureStore, TFeatureScopeIdProvider, TFeatureScopeId>(params string[] featureNames)
    : EngardeAttribute(featureNames)
    where TFeatureStore : notnull
    where TFeatureScopeIdProvider : notnull
{
    internal override async ValueTask<bool> EvaluateAsync(EngardeEvaluation evaluation, string featureName, MemberInfo member, object? value)
    {
        var settings = EvaluationCacheKey;
        var scopeId = await evaluation.ScopeIdAsync(new EngardeEvaluation.ScopeKey(GetType(), settings),
            (Guard: this, evaluation.Services), static async state =>
                await state.Guard.GetFeatureScopeIdAsync(state.Services.GetRequiredService<TFeatureScopeIdProvider>()).ConfigureAwait(false))
            .ConfigureAwait(false);
        return await evaluation.CheckAsync(new EngardeEvaluation.CheckKey(GetType(), featureName, scopeId, settings),
            (Guard: this, evaluation.Services, featureName, scopeId), static state =>
                state.Guard.IsEnabledAsync(state.Guard.ResolveFeatureStore(state.Services), state.featureName, (TFeatureScopeId)state.scopeId!))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves this guard's feature store. The default uses the current DI scope; override to return another
    /// implementation or a fixed instance.
    /// </summary>
    protected virtual TFeatureStore ResolveFeatureStore(IServiceProvider services) =>
        services.GetRequiredService<TFeatureStore>();

    protected abstract ValueTask<TFeatureScopeId> GetFeatureScopeIdAsync(TFeatureScopeIdProvider provider);

    protected abstract ValueTask<bool> IsEnabledAsync(TFeatureStore store, string featureName, TFeatureScopeId scopeId);

    protected internal sealed override async ValueTask<bool> IsEnabledAsync(string featureName, EngardeContext context)
    {
        var scopeId = await context.GetOrAddFeatureScopeIdAsync(
            this, async () => await GetFeatureScopeIdAsync(context.GetRequiredService<TFeatureScopeIdProvider>()));

        return await IsEnabledAsync(ResolveFeatureStore(context.Services), featureName, (TFeatureScopeId)scopeId!);
    }
}

internal sealed record ScopeId(Type Type, Func<ValueTask<object?>> Resolve);
