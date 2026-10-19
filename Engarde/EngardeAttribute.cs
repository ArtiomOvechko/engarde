using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Engarde;

/// <summary>
/// Validates features when the guarded member is used. Woven into the code at build time, so it applies
/// to every call or assignment in a woven assembly, including a class calling its own members:
/// <list type="bullet">
/// <item>Method / constructor: on each call.</item>
/// <item>Class / struct / record: on every emitted instance/static constructor and ordinary method call,
/// including implicit class constructors (property accessors and other compiler-generated methods are skipped).
/// Derived classes' ordinary methods are guarded too.</item>
/// <item>Interface: like a class guard, on every constructor and ordinary method of a class or struct that implements
/// it, directly or through another interface, and on its default implementations.</item>
/// <item>Property: when the setter (or init) is given a non-empty value.</item>
/// <item>Field: when a non-empty value is assigned.</item>
/// <item>Parameter: when the method is called with a non-empty argument.</item>
/// </list>
/// Guards on a method, property or parameter also apply to the members overriding or implementing it, including
/// methods inherited from a base class that doesn't implement the interface (when called through the interface).
/// Features are evaluated and failures handled as registered with <c>AddEngarde</c>. To replace that for specific members, derive from:
/// <list type="bullet">
/// <item><see cref="EngardeAttribute{TFeatureScopeIdProvider, TFeatureScopeId}"/>: custom feature scope.</item>
/// <item><see cref="EngardeAttribute{TFeatureStore}"/>: custom feature store.</item>
/// <item><see cref="EngardeAttribute{TFeatureStore, TFeatureScopeIdProvider, TFeatureScopeId}"/>: custom feature scope and store.</item>
/// <item>This attribute, overriding <see cref="IsEnabledAsync"/> and/or <see cref="OnFeatureMissing"/> directly.</item>
/// </list>
/// A guard that overrides both methods and does not resolve services from <see cref="EngardeContext.Services"/>
/// is self-contained and can run without registering <c>AddEngarde</c>.
/// </summary>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Method
        | AttributeTargets.Constructor | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter,
    AllowMultiple = true,
    Inherited = false)]
public class EngardeAttribute(params string[] featureNames) : Attribute
{
    public virtual IReadOnlyList<string> FeatureNames { get; } = featureNames;

    /// <summary>
    /// Whether the feature is enabled. Defaults to the registered feature store (with this attribute's feature scope id).
    /// </summary>
    protected internal virtual ValueTask<bool> IsEnabledAsync(string featureName, EngardeContext context, CancellationToken cancellationToken)
    {
        return context.IsEnabledByDefaultAsync(featureName, this);
    }

    /// <summary>
    /// Called for each disabled feature. Throwing blocks a method or parameter guard; returning lets that call proceed.
    /// A disabled property or field write is skipped after this method returns. Defaults to the registered handler.
    /// Inline guard queries call it too, unless the guard is marked with <see cref="SkipInlineFailuresAttribute"/>.
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

    internal ValueTask<bool> EvaluateAsync(
        EngardeEvaluation evaluation, string featureName, MemberInfo member, object? value, ScopeIdOverride? scopeId) =>
        GetType() == typeof(EngardeAttribute) ? evaluation.DefaultAsync(featureName, scopeId)
            : evaluation.CheckGuardAsync(this, featureName, member, value, scopeId);

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
    protected abstract ValueTask<TFeatureScopeId> GetScopeIdAsync(TFeatureScopeIdProvider provider, CancellationToken cancellationToken);

    private protected sealed override ScopeId ResolveFeatureScopeId(EngardeContext context)
    {
        return new ScopeId(
            typeof(TFeatureScopeId),
            async () => await GetScopeIdAsync(context.GetRequiredService<TFeatureScopeIdProvider>(), context.CancellationToken));
    }
}

/// <summary>
/// Evaluates features with its own feature store, without a feature scope; the registered feature store is not used
/// </summary>
public abstract class EngardeAttribute<TFeatureStore>(params string[] featureNames) : EngardeAttribute(featureNames)
    where TFeatureStore : notnull
{
    /// <summary>
    /// Resolves this guard's feature store. The default uses the current DI scope; override to return another
    /// implementation or a fixed instance. A fixed instance works without <c>AddEngarde</c>.
    /// </summary>
    protected virtual TFeatureStore ResolveFeatureStore(IServiceProvider services) =>
        (services ?? throw EngardeContext.NoServices()).GetRequiredService<TFeatureStore>();

    protected abstract ValueTask<bool> IsEnabledAsync(TFeatureStore store, string featureName, CancellationToken cancellationToken);

    protected internal sealed override ValueTask<bool> IsEnabledAsync(string featureName, EngardeContext context, CancellationToken cancellationToken)
    {
        if (context.ScopeIdOverride is not null)
            throw new InvalidOperationException($"{GetType().Name} evaluates features without a feature scope id");
        return IsEnabledAsync(ResolveFeatureStore(context.OptionalServices!), featureName, cancellationToken);
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
    /// <summary>
    /// Resolves this guard's feature store. The default uses the current DI scope; override to return another
    /// implementation or a fixed instance. A fixed instance works without <c>AddEngarde</c>.
    /// </summary>
    protected virtual TFeatureStore ResolveFeatureStore(IServiceProvider services) =>
        (services ?? throw EngardeContext.NoServices()).GetRequiredService<TFeatureStore>();

    protected abstract ValueTask<TFeatureScopeId> GetScopeIdAsync(TFeatureScopeIdProvider provider, CancellationToken cancellationToken);

    protected abstract ValueTask<bool> IsEnabledAsync(
        TFeatureStore store, string featureName, TFeatureScopeId scopeId, CancellationToken cancellationToken);

    protected internal sealed override async ValueTask<bool> IsEnabledAsync(
        string featureName, EngardeContext context, CancellationToken cancellationToken)
    {
        var scopeId = context.ScopeIdOverride is { } scopeIdOverride
            ? scopeIdOverride.Get(typeof(TFeatureScopeId), GetType())
            : await GetScopeIdAsync(context.GetRequiredService<TFeatureScopeIdProvider>(), cancellationToken);

        return await IsEnabledAsync(ResolveFeatureStore(context.OptionalServices!), featureName, (TFeatureScopeId)scopeId!,
            cancellationToken);
    }
}

internal sealed record ScopeId(Type Type, Func<ValueTask<object?>> Resolve);

internal sealed record ScopeIdOverride(object? Value)
{
    internal object? Get(Type expected, Type guard)
    {
        if (Value is null ? !expected.IsValueType || Nullable.GetUnderlyingType(expected) is not null : expected.IsInstanceOfType(Value))
            return Value;
        throw new InvalidOperationException(
            $"{guard.Name} expects a {expected.Name} feature scope id, but {Value?.GetType().Name ?? "null"} was provided");
    }
}
