using Engarde;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Guard = Engarde.Engarde;

namespace Tests;

public sealed class EvaluationTests
{
    [Fact]
    public async Task Every_check_resolves_the_scope_id_and_calls_the_store()
    {
        var resolutions = 0;
        var calls = 0;
        await using var services = new ServiceCollection().AddSingleton(new Store()).AddScoped<ScopeProvider>()
            .AddEngarde(builder => builder.UseFeatureStore<Store, ScopeProvider, int>(_ =>
            {
                Interlocked.Increment(ref resolutions);
                return ValueTask.FromResult(7);
            }, (_, _, id) => { Interlocked.Increment(ref calls); return ValueTask.FromResult(id == 7); })
            .OnFeatureMissing((_, _) => { })).BuildServiceProvider();
        using var active = Guard.BeginScope(services);
        var results = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => Guard.CreateInstance().IsFeatureEnabledAsync("flag").AsTask())));
        Assert.All(results, Assert.True);
        Assert.Equal(32, resolutions);
        Assert.Equal(32, calls);
    }

    [Fact]
    public async Task Equal_settings_from_different_evaluators_do_not_collide()
    {
        var store = new Store();
        await using var services = new ServiceCollection().AddSingleton(store).AddScoped<ScopeProvider>()
            .AddEngarde(builder => builder.UseFeatureStore<Store>((_, _) => throw new Exception("Use the custom store"))
                .OnFeatureMissing((_, _) => { })).BuildServiceProvider();
        using var active = Guard.BeginScope(services);
        Assert.True(await Guard.CreateInstance().IsFeatureEnabledAsync(new AllowGuard(), "flag"));
        Assert.False(await Guard.CreateInstance().IsFeatureEnabledAsync(new DenyGuard(), "flag"));
        Assert.True(await Guard.CreateInstance().IsFeatureEnabledAsync(new AllowScopeGuard(), "flag"));
        Assert.False(await Guard.CreateInstance().IsFeatureEnabledAsync(new DenyScopeGuard(), "flag"));
        Assert.True(await Guard.CreateInstance().IsFeatureEnabledAsync(new AllowScopeGuard(), "flag"));
        Assert.Equal(5, store.CustomCalls);
    }

    [Fact]
    public async Task Custom_guards_run_for_every_operation()
    {
        await using var services = new ServiceCollection().AddSingleton(new Store())
            .AddEngarde(builder => builder.UseFeatureStore<Store>((_, _) => ValueTask.FromResult(true))
                .OnFeatureMissing((_, _) => { })).BuildServiceProvider();
        using var active = Guard.BeginScope(services);
        var first = new IdentityGuard();
        var second = new IdentityGuard();
        Assert.True(await Guard.CreateInstance().IsFeatureEnabledAsync(first, "flag"));
        Assert.True(await Guard.CreateInstance().IsFeatureEnabledAsync(second, "flag"));
        Assert.True(await Guard.CreateInstance().IsFeatureEnabledAsync(first, "flag"));
        Assert.Equal(2, first.Calls);
        Assert.Equal(1, second.Calls);
    }

    [Fact]
    public async Task Guarded_values_are_checked_for_every_assignment()
    {
        var storeCalls = 0;
        await using var services = new ServiceCollection().AddSingleton(new Store())
            .AddEngarde(builder => builder.UseFeatureStore<Store>((_, _) =>
                { storeCalls++; return ValueTask.FromResult(true); })
                .OnFeatureMissing((_, _) => { })).BuildServiceProvider();
        using var active = Guard.BeginScope(services);
        ValueSensitiveGuardAttribute.Calls = 0;
        var subject = new ValueSensitiveSubject();

        subject.Value = "allow";
        subject.Value = "deny";
        subject.Value = "allow";

        Assert.Equal("allow", subject.Value);
        Assert.Equal(3, ValueSensitiveGuardAttribute.Calls);
        Assert.Equal(2, storeCalls); // Only allowed values reach the store
    }

    private sealed class IdentityGuard : EngardeAttribute
    {
        public int Calls;
        protected override ValueTask<bool> IsEnabledAsync(string featureName, EngardeContext context)
        { Calls++; return ValueTask.FromResult(true); }
    }

    private sealed class AllowGuard : EngardeAttribute<Store>
    {
        protected override ValueTask<bool> IsEnabledAsync(Store store, string featureName)
        { store.CustomCalls++; return ValueTask.FromResult(true); }
    }

    private sealed class DenyGuard : EngardeAttribute<Store>
    {
        protected override ValueTask<bool> IsEnabledAsync(Store store, string featureName)
        { store.CustomCalls++; return ValueTask.FromResult(false); }
    }

    private sealed class AllowScopeGuard : EngardeAttribute<Store, ScopeProvider, int>
    {
        protected override ValueTask<int> GetFeatureScopeIdAsync(ScopeProvider provider) => ValueTask.FromResult(7);
        protected override ValueTask<bool> IsEnabledAsync(Store store, string featureName, int scopeId)
        { store.CustomCalls++; return ValueTask.FromResult(scopeId == 7); }
    }

    private sealed class DenyScopeGuard : EngardeAttribute<Store, ScopeProvider, int>
    {
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
