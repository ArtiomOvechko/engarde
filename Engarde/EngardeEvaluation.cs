using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace Engarde;

/// <summary>A bounded evaluation session. Nothing is cached beyond its lifetime.</summary>
internal sealed class EngardeEvaluation : IDisposable, IAsyncDisposable
{
    private static readonly AsyncLocal<EngardeEvaluation?> Ambient = new();
    private static readonly IServiceProvider EmptyServices = new EmptyServiceProvider();
    private readonly AsyncServiceScope? _ownedScope;
    private readonly IServiceProvider? _sourceServices;
    private readonly SourceKind _sourceKind;
    internal IServiceProvider Services { get; }
    private readonly EngardeConfiguration? _configuration;
    private ConcurrentDictionary<CheckKey, Lazy<Task<bool>>>? _checks;
    private ConcurrentDictionary<ScopeKey, Lazy<Task<object?>>>? _scopeIds;
    private readonly EngardeEvaluation? _previous;
    private readonly bool _ambient;
    private bool _disposed;

    private EngardeEvaluation(bool ambient, IServiceProvider? requestServices = null, bool createScope = false)
    {
        if (requestServices is not null)
        {
            _sourceServices = requestServices;
            _sourceKind = SourceKind.Explicit;
            _configuration = requestServices.GetRequiredService<EngardeConfiguration>();
            if (createScope)
            {
                _ownedScope = requestServices.CreateAsyncScope();
                Services = _ownedScope.Value.ServiceProvider;
            }
            else
                Services = requestServices;
        }
        else if (global::Engarde.Engarde.TryGetCurrent(out var current))
        {
            _sourceServices = current.Services;
            _sourceKind = current.CreateScope ? SourceKind.Owned : SourceKind.Scoped;
            _configuration = current.Configuration;
            if (current.CreateScope)
            {
                _ownedScope = current.Services.CreateAsyncScope();
                Services = _ownedScope.Value.ServiceProvider;
            }
            else
                Services = current.Services;
        }
        else
            Services = EmptyServices;
        _ambient = ambient;
        if (ambient)
        {
            _previous = Ambient.Value;
            Ambient.Value = this;
        }
    }

    internal static EngardeEvaluation Begin() => new(true);
    internal static EngardeEvaluation Begin(IServiceProvider services) => new(true, services);

    internal static ValueTask<bool> QueryAsync(string featureName, IServiceProvider? services, bool createScope)
    {
        var current = GetAmbient(services, createScope);
        if (current is null)
            return QueryOnceAsync(featureName, services, createScope);
        return current.DefaultAsync(featureName);
    }

    private static async ValueTask<bool> QueryOnceAsync(string featureName, IServiceProvider? provider, bool createScope)
    {
        if (provider is null)
        {
            if (!global::Engarde.Engarde.TryGetCurrent(out var current))
                throw new InvalidOperationException("Feature guards are not active, register them with AddEngarde");
            if (!current.CreateScope)
                return await EvaluateOnceAsync(current.Services, current.Configuration, featureName).ConfigureAwait(false);
            await using var currentScope = current.Services.CreateAsyncScope();
            return await EvaluateOnceAsync(currentScope.ServiceProvider, current.Configuration, featureName).ConfigureAwait(false);
        }
        var services = provider;
        var configuration = provider.GetRequiredService<EngardeConfiguration>();
        if (!createScope)
            return await EvaluateOnceAsync(services, configuration, featureName).ConfigureAwait(false);
        await using var scope = services.CreateAsyncScope();
        return await EvaluateOnceAsync(scope.ServiceProvider, configuration, featureName).ConfigureAwait(false);
    }

    private static async ValueTask<bool> EvaluateOnceAsync(IServiceProvider services, EngardeConfiguration configuration, string featureName)
    {
        var store = configuration.FeatureStore;
        var scopeId = store.ScopeIdType is null ? null
            : await store.ScopeIdFactory!(services).ConfigureAwait(false);
        return await store.IsEnabled(services, featureName, scopeId).ConfigureAwait(false);
    }

    private static EngardeEvaluation? GetAmbient(IServiceProvider? services, bool createScope)
    {
        var current = Ambient.Value;
        if (current is null)
            return null;

        if (services is not null)
        {
            var configuration = services.GetRequiredService<EngardeConfiguration>();
            // Singleton query services intentionally join an active request evaluation from the same container.
            if (!ReferenceEquals(current._configuration, configuration)
                || (!createScope && !ReferenceEquals(current._sourceServices, services)))
                return null;
        }
        else if (global::Engarde.Engarde.TryGetScopedServices(out var scoped))
        {
            var expectedKind = scoped.CreateScope ? SourceKind.Owned : SourceKind.Scoped;
            if (!ReferenceEquals(current._sourceServices, scoped.Services)
                || !ReferenceEquals(current._configuration, scoped.Configuration)
                || current._sourceKind != expectedKind)
                return null;
        }
        else if (current._sourceKind == SourceKind.Owned)
        {
            if (!global::Engarde.Engarde.TryGetGlobalServices(out var global)
                || !ReferenceEquals(current._sourceServices, global)
                || !ReferenceEquals(current._configuration, global.GetRequiredService<EngardeConfiguration>()))
                return null;
        }
        current.ThrowIfDisposed();
        return current;
    }

    internal static Lease Acquire(IServiceProvider? services = null, bool createScope = false)
    {
        var current = GetAmbient(services, createScope);
        return new Lease(current ?? new EngardeEvaluation(false, services, createScope), current is null);
    }

    internal EngardeContext CreateContext(MemberInfo member, object? value) =>
        new(_configuration, Services, member, value, this);

    internal async ValueTask<bool> DefaultAsync(string featureName)
    {
        var store = RequiredConfiguration.FeatureStore;
        var scopeId = store.ScopeIdType is null ? null
            : await ScopeIdAsync(new ScopeKey(store), (store, Services),
                static state => state.store.ScopeIdFactory!(state.Services)).ConfigureAwait(false);
        return await CheckStoreAsync(store, featureName, scopeId).ConfigureAwait(false);
    }

    internal ValueTask<bool> CheckStoreAsync(FeatureStoreRegistration store, string featureName, object? scopeId) =>
        CheckAsync(new CheckKey(store, featureName, scopeId), (store, Services, featureName, scopeId),
            static state => state.store.IsEnabled(state.Services, state.featureName, state.scopeId));

    internal ValueTask<bool> CheckGuardAsync(EngardeAttribute guard, string featureName, MemberInfo member, object? value) =>
        guard.IsEnabledAsync(featureName, CreateContext(member, value));

    internal ValueTask<bool> CheckAsync<TState>(CheckKey key, TState state, Func<TState, ValueTask<bool>> evaluate)
    {
        var checks = GetCache(ref _checks);
        if (checks.TryGetValue(key, out var cached))
            return new(cached.Value);
        return new(checks.GetOrAdd(key, static (_, input) =>
            new Lazy<Task<bool>>(() => input.evaluate(input.state).AsTask()), (state, evaluate)).Value);
    }

    internal ValueTask<object?> ScopeIdAsync<TState>(ScopeKey key, TState state, Func<TState, ValueTask<object?>> resolve)
    {
        var scopeIds = GetCache(ref _scopeIds);
        if (scopeIds.TryGetValue(key, out var cached))
            return new(cached.Value);
        return new(scopeIds.GetOrAdd(key, static (_, input) =>
            new Lazy<Task<object?>>(() => input.resolve(input.state).AsTask()), (state, resolve)).Value);
    }

    private static ConcurrentDictionary<TKey, TValue> GetCache<TKey, TValue>(ref ConcurrentDictionary<TKey, TValue>? cache)
        where TKey : notnull
    {
        var current = Volatile.Read(ref cache);
        if (current is not null) return current;
        var created = new ConcurrentDictionary<TKey, TValue>(concurrencyLevel: 1, capacity: 8);
        return Interlocked.CompareExchange(ref cache, created, null) ?? created;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(EngardeEvaluation));
    }

    private void End()
    {
        if (_ambient)
            Ambient.Value = _previous;
        _disposed = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        End();
        if (_ownedScope is { } scope)
            Wait(scope, static async owned => { await owned.DisposeAsync().ConfigureAwait(false); return true; });
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return default;
        End();
        return _ownedScope?.DisposeAsync() ?? default;
    }

    internal static T Wait<TState, T>(TState state, Func<TState, ValueTask<T>> operation)
    {
        // Attribute checks must also work on threads with a single-threaded synchronization context.
        if (SynchronizationContext.Current is not null)
            return WaitOnContext(state, operation);
        var task = operation(state);
        return task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
    }

    private static T WaitOnContext<TState, T>(TState state, Func<TState, ValueTask<T>> operation) =>
        Task.Run(async () => await operation(state).ConfigureAwait(false)).GetAwaiter().GetResult();

    // Evaluators/resolvers use identity; custom settings and scope IDs use value equality.
    private static bool SettingsEqual(object? left, object? right) =>
        left is EngardeAttribute || right is EngardeAttribute ? ReferenceEquals(left, right) : Equals(left, right);

    private static int SettingsHash(object? settings) => settings is EngardeAttribute
        ? RuntimeHelpers.GetHashCode(settings) : settings?.GetHashCode() ?? 0;

    internal readonly record struct CheckKey(object Evaluator, string FeatureName, object? ScopeId, object? Settings = null)
    {
        public bool Equals(CheckKey other) => ReferenceEquals(Evaluator, other.Evaluator)
            && FeatureName == other.FeatureName && Equals(ScopeId, other.ScopeId) && SettingsEqual(Settings, other.Settings);
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = RuntimeHelpers.GetHashCode(Evaluator);
                hash = (hash * 397) ^ FeatureName.GetHashCode();
                hash = (hash * 397) ^ (ScopeId?.GetHashCode() ?? 0);
                return (hash * 397) ^ SettingsHash(Settings);
            }
        }
    }

    internal readonly record struct ScopeKey(object Resolver, object? Settings = null)
    {
        public bool Equals(ScopeKey other) => ReferenceEquals(Resolver, other.Resolver) && SettingsEqual(Settings, other.Settings);
        public override int GetHashCode()
        {
            unchecked
            {
                return (RuntimeHelpers.GetHashCode(Resolver) * 397) ^ SettingsHash(Settings);
            }
        }
    }

    internal readonly struct Lease(EngardeEvaluation state, bool owns) : IDisposable, IAsyncDisposable
    {
        public EngardeEvaluation State => state;
        public void Dispose() { if (owns) state.Dispose(); }
        public ValueTask DisposeAsync() => owns ? state.DisposeAsync() : default;
    }

    private EngardeConfiguration RequiredConfiguration => _configuration
        ?? throw new InvalidOperationException(
            "This guard uses the default feature store. Register it with AddEngarde, or override IsEnabledAsync and OnFeatureMissing in the guard.");

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private enum SourceKind { None, Explicit, Scoped, Owned }
}
