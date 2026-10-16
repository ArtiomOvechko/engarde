using Engarde;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Guard = Engarde.Engarde;

namespace Tests;

public sealed class EvaluationCacheTests
{
    [Fact]
    public async Task Concurrent_misses_share_the_same_in_flight_store_evaluation()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var services = new ServiceCollection().AddSingleton(new Store())
            .AddEngarde(builder => builder.UseFeatureStore<Store>((_, _) =>
            {
                Interlocked.Increment(ref calls);
                return new ValueTask<bool>(completion.Task);
            }).OnFeatureMissing((_, _) => { })).BuildServiceProvider();
        using var active = Guard.BeginScope(services);
        using var evaluation = Guard.BeginEvaluationScope();
        var pending = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => Guard.CreateInstance().IsEnabledAsync("flag"))));
        Assert.Equal(1, calls);
        Assert.All(pending, query => Assert.False(query.IsCompleted));
        completion.SetResult(true);
        Assert.All(await Task.WhenAll(pending.Select(query => query.AsTask())), Assert.True);
    }

    [Fact]
    public async Task Concurrent_flags_share_an_asynchronous_scope_id_resolution()
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolutions = 0;
        var calls = 0;
        await using var services = new ServiceCollection().AddSingleton(new Store()).AddScoped<ScopeProvider>()
            .AddEngarde(builder => builder.UseFeatureStore<Store, ScopeProvider, int>(_ =>
            {
                Interlocked.Increment(ref resolutions);
                return new ValueTask<int>(completion.Task);
            }, (_, _, id) => { Interlocked.Increment(ref calls); return ValueTask.FromResult(id == 7); })
            .OnFeatureMissing((_, _) => { })).BuildServiceProvider();
        using var active = Guard.BeginScope(services);
        using var evaluation = Guard.BeginEvaluationScope();
        var pending = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() => Guard.CreateInstance().IsEnabledAsync("flag" + i % 2))));
        Assert.Equal(1, resolutions);
        Assert.Equal(0, calls);
        completion.SetResult(7);
        Assert.All(await Task.WhenAll(pending.Select(query => query.AsTask())), Assert.True);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Equal_settings_from_different_evaluators_do_not_collide()
    {
        var store = new Store();
        await using var services = new ServiceCollection().AddSingleton(store).AddScoped<ScopeProvider>()
            .AddEngarde(builder => builder.UseFeatureStore<Store>((_, _) => throw new Exception("Use the custom store"))
                .OnFeatureMissing((_, _) => { })).BuildServiceProvider();
        using var active = Guard.BeginScope(services);
        using var evaluation = Guard.BeginEvaluationScope();
        Assert.True(await Guard.CreateInstance().IsEnabledAsync(new AllowGuard(), "flag"));
        Assert.False(await Guard.CreateInstance().IsEnabledAsync(new DenyGuard(), "flag"));
        Assert.True(await Guard.CreateInstance().IsEnabledAsync(new AllowScopeGuard(), "flag"));
        Assert.False(await Guard.CreateInstance().IsEnabledAsync(new DenyScopeGuard(), "flag"));
        Assert.True(await Guard.CreateInstance().IsEnabledAsync(new AllowScopeGuard(), "flag"));
        Assert.Equal(4, store.CustomCalls);
    }

    [Fact]
    public async Task Context_dependent_custom_guards_run_for_every_operation()
    {
        await using var services = new ServiceCollection().AddSingleton(new Store())
            .AddEngarde(builder => builder.UseFeatureStore<Store>((_, _) => ValueTask.FromResult(true))
                .OnFeatureMissing((_, _) => { })).BuildServiceProvider();
        using var active = Guard.BeginScope(services);
        using var evaluation = Guard.BeginEvaluationScope();
        var first = new IdentityGuard();
        var second = new IdentityGuard();
        Assert.True(await Guard.CreateInstance().IsEnabledAsync(first, "flag"));
        Assert.True(await Guard.CreateInstance().IsEnabledAsync(second, "flag"));
        Assert.True(await Guard.CreateInstance().IsEnabledAsync(first, "flag"));
        Assert.Equal(2, first.Calls);
        Assert.Equal(1, second.Calls);
        _ = first.GetHashCode(); // Key memoization must not introduce a self-referential Attribute field.
    }

    [Fact]
    public async Task Guarded_values_are_not_hidden_by_cached_underlying_store_results()
    {
        var storeCalls = 0;
        await using var services = new ServiceCollection().AddSingleton(new Store())
            .AddEngarde(builder => builder.UseFeatureStore<Store>((_, _) =>
                { storeCalls++; return ValueTask.FromResult(true); })
                .OnFeatureMissing((_, _) => { })).BuildServiceProvider();
        using var active = Guard.BeginScope(services);
        using var evaluation = Guard.BeginEvaluationScope();
        ValueSensitiveGuardAttribute.Calls = 0;
        var subject = new ValueSensitiveSubject();

        subject.Value = "allow";
        subject.Value = "deny";
        subject.Value = "allow";

        Assert.Equal("allow", subject.Value);
        Assert.Equal(3, ValueSensitiveGuardAttribute.Calls);
        Assert.Equal(1, storeCalls);
    }

    private sealed class IdentityGuard : EngardeAttribute
    {
        public int Calls;
        protected override ValueTask<bool> IsEnabledAsync(string featureName, EngardeContext context)
        { Calls++; return ValueTask.FromResult(true); }
    }

    private sealed class AllowGuard : EngardeAttribute<Store>
    {
        protected override object CacheKey => "same-settings";
        protected override ValueTask<bool> IsEnabledAsync(Store store, string featureName)
        { store.CustomCalls++; return ValueTask.FromResult(true); }
    }

    private sealed class DenyGuard : EngardeAttribute<Store>
    {
        protected override object CacheKey => "same-settings";
        protected override ValueTask<bool> IsEnabledAsync(Store store, string featureName)
        { store.CustomCalls++; return ValueTask.FromResult(false); }
    }

    private sealed class AllowScopeGuard : EngardeAttribute<Store, ScopeProvider, int>
    {
        protected override object CacheKey => "same-settings";
        protected override ValueTask<int> GetFeatureScopeIdAsync(ScopeProvider provider) => ValueTask.FromResult(7);
        protected override ValueTask<bool> IsEnabledAsync(Store store, string featureName, int scopeId)
        { store.CustomCalls++; return ValueTask.FromResult(scopeId == 7); }
    }

    private sealed class DenyScopeGuard : EngardeAttribute<Store, ScopeProvider, int>
    {
        protected override object CacheKey => "same-settings";
        protected override ValueTask<int> GetFeatureScopeIdAsync(ScopeProvider provider) => ValueTask.FromResult(8);
        protected override ValueTask<bool> IsEnabledAsync(Store store, string featureName, int scopeId)
        { store.CustomCalls++; return ValueTask.FromResult(scopeId == 7); }
    }
}

public sealed class ValueSensitiveGuardAttribute() : EngardeAttribute("value-sensitive")
{
    public static int Calls;

    protected override async ValueTask<bool> IsEnabledAsync(string featureName, EngardeContext context)
    {
        Calls++;
        return Equals(context.Value, "allow")
            && await base.IsEnabledAsync(featureName, context);
    }

    protected override void OnFeatureMissing(string featureName, EngardeContext context) { }
}

public sealed class ValueSensitiveSubject
{
    [ValueSensitiveGuard] public string? Value;
}
