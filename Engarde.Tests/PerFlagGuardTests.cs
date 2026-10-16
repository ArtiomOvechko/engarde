using Engarde;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Tests;

public sealed class PerFlagGuardTests
{
    [Fact]
    public async Task Encapsulated_flags_inherit_registered_store_scope_and_missing_handler()
    {
        var calls = new List<(string Flag, int Scope)>();
        var resolutions = 0;
        await using var services = new ServiceCollection().AddSingleton(new Store()).AddScoped<ScopeProvider>()
            .AddEngarde(builder => builder.UseFeatureStore<Store, ScopeProvider, int>(
                provider => { resolutions++; return ValueTask.FromResult(provider.Id); },
                (_, flag, scope) => { calls.Add((flag, scope)); return ValueTask.FromResult(flag == "enabled" && scope == 7); })
                .OnFeatureMissing((flag, _) => throw new DisabledFeatureException(flag)))
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var active = Engarde.Engarde.BeginScope(services);
        using var evaluation = Engarde.Engarde.BeginEvaluationScope();

        Assert.True(await EnabledEngarde.CreateInstance().IsEnabledAsync());
        Assert.True(await EnabledEngarde.CreateInstance().IsEnabledAsync());
        Assert.True(await DerivedEnabledGuard.CreateInstance().IsEnabledAsync());
        Assert.True(await Engarde.Engarde.CreateInstance().IsEnabledAsync(new EnabledEngarde()));
        Assert.True(await Engarde.Engarde.CreateInstance().IsEnabledAsync(new EnabledEngarde()));
        PerFlagHandlers.Enabled();
        Assert.False(await DisabledEngarde.CreateInstance().IsEnabledAsync());
        Assert.False(await DisabledEngarde.CreateInstance().IsEnabledAsync());
        Assert.Throws<DisabledFeatureException>(PerFlagHandlers.Disabled);
        Assert.Equal(new[] { ("enabled", 7), ("disabled", 7) }, calls);
        Assert.Equal(1, resolutions);
    }

    [Fact]
    public async Task Encapsulated_flags_keep_custom_constructor_and_scope_override()
    {
        var scopes = new List<int>();
        await using var services = new ServiceCollection().AddSingleton(new Store()).AddSingleton(new ScopeProvider())
            .AddEngarde(builder => builder.UseFeatureStore<Store, ScopeProvider, int>(
                _ => ValueTask.FromResult(7),
                (_, flag, id) => { scopes.Add(id); return ValueTask.FromResult(flag == "enabled" && id == 7); })
                .OnFeatureMissing((flag, _) => throw new DisabledFeatureException(flag))).BuildServiceProvider();
        using var active = Engarde.Engarde.BeginScope(services);
        using var evaluation = Engarde.Engarde.BeginEvaluationScope();
        Assert.True(await SelectedFlagGuard.CreateInstance().IsEnabledAsync(true));
        Assert.False(await SelectedFlagGuard.CreateInstance().IsEnabledAsync(false));
        Assert.True(await FixedScopeGuard.CreateInstance().IsEnabledAsync(7));
        Assert.False(await FixedScopeGuard.CreateInstance().IsEnabledAsync(8));
        Assert.True(await NamedScopeFlagGuard.CreateInstance().IsEnabledAsync("default"));
        Assert.False(await NamedScopeFlagGuard.CreateInstance().IsEnabledAsync("other"));
        Assert.Throws<DisabledFeatureException>(PerFlagHandlers.Scoped);
        Assert.Equal(new[] { 7, 7, 8 }, scopes);
    }

    [Fact]
    public async Task Encapsulated_flags_keep_custom_store_and_custom_store_with_scope()
    {
        var store = new Store();
        var scopeProvider = new ScopeProvider();
        await using var services = new ServiceCollection().AddSingleton(store).AddSingleton(scopeProvider)
            .AddEngarde(builder => builder.UseFeatureStore<Store>((_, _) => throw new Exception("Default store must be overridden"))
                .OnFeatureMissing((flag, _) => throw new DisabledFeatureException(flag))).BuildServiceProvider();
        using var active = Engarde.Engarde.BeginScope(services);
        using var evaluation = Engarde.Engarde.BeginEvaluationScope();
        Assert.True(await FixedVariantGuard.CreateInstance().IsEnabledAsync(FeatureVariant.Primary));
        Assert.True(await FixedVariantGuard.CreateInstance().IsEnabledAsync(FeatureVariant.Primary));
        Assert.False(await FixedVariantGuard.CreateInstance().IsEnabledAsync(FeatureVariant.Secondary));
        PerFlagHandlers.VariantEnabled();
        Assert.Equal(2, store.CustomCalls);

        Assert.True(await FixedStoreScopeGuard.CreateInstance().IsEnabledAsync(FeatureVariant.Primary));
        Assert.True(await FixedStoreScopeGuard.CreateInstance().IsEnabledAsync(FeatureVariant.Primary));
        Assert.False(await FixedStoreScopeGuard.CreateInstance().IsEnabledAsync(FeatureVariant.Secondary));
        Assert.Equal(4, store.CustomCalls);
        Assert.Equal(2, scopeProvider.Resolutions);
    }

    [Fact]
    public async Task Encapsulated_multiple_flags_use_all_of_semantics_without_invoking_failure_handler()
    {
        var store = new Store();
        await using var services = new ServiceCollection().AddSingleton(store)
            .AddEngarde(builder => builder.UseFeatureStore<Store>((s, flag) => s.Enabled(flag))
                .OnFeatureMissing((_, _) => throw new Exception("Inline checks must not invoke handler"))).BuildServiceProvider();
        using var active = Engarde.Engarde.BeginScope(services);
        using var evaluation = Engarde.Engarde.BeginEvaluationScope();
        Assert.False(await CombinedEngarde.CreateInstance().IsEnabledAsync());
        Assert.False(await CombinedEngarde.CreateInstance().IsEnabledAsync());
        Assert.Equal(2, store.Calls);
        await Assert.ThrowsAsync<ArgumentNullException>(() => Engarde.Engarde.CreateInstance().IsEnabledAsync((EngardeAttribute)null!).AsTask());
    }
}

public sealed partial class EnabledEngarde() : EngardeAttribute("enabled");
public sealed partial class DisabledEngarde() : EngardeAttribute("disabled");
public abstract class BaseEnabledGuard() : EngardeAttribute("enabled");
public sealed partial class DerivedEnabledGuard() : BaseEnabledGuard;
public sealed partial class CombinedEngarde() : EngardeAttribute("disabled", "enabled");
public sealed partial class SelectedFlagGuard(bool enabled) : EngardeAttribute(enabled ? "enabled" : "disabled");

public sealed partial class FixedScopeGuard(int id) : EngardeAttribute<ScopeProvider, int>("enabled")
{
    protected override object CacheKey => (GetType(), id);
    protected override ValueTask<int> GetFeatureScopeIdAsync(ScopeProvider provider) => ValueTask.FromResult(id);
}

public sealed partial class NamedScopeFlagGuard(string featurePool) : EngardeAttribute<ScopeProvider, int>("enabled")
{
    protected override ValueTask<int> GetFeatureScopeIdAsync(ScopeProvider provider) => ValueTask.FromResult(featurePool == "default" ? 7 : 8);
}

public sealed partial class FixedVariantGuard(FeatureVariant variant) : EngardeAttribute<Store>("new-experience")
{
    protected override object CacheKey => (GetType(), variant);
    protected override ValueTask<bool> IsEnabledAsync(Store store, string featureName) => store.Enabled(variant, featureName);
}

public sealed partial class FixedStoreScopeGuard(FeatureVariant variant) : EngardeAttribute<Store, ScopeProvider, FeatureVariant>("new-experience")
{
    protected override object CacheKey => (GetType(), variant);
    protected override ValueTask<FeatureVariant> GetFeatureScopeIdAsync(ScopeProvider provider)
    {
        provider.Resolutions++;
        return ValueTask.FromResult(variant);
    }
    protected override ValueTask<bool> IsEnabledAsync(Store store, string featureName, FeatureVariant scopeId) => store.Enabled(scopeId, featureName);
}

public static class PerFlagHandlers
{
    [EnabledEngarde] public static void Enabled() { }
    [DisabledEngarde] public static void Disabled() { }
    [FixedScopeGuard(8)] public static void Scoped() { }
    [FixedVariantGuard(FeatureVariant.Primary)] public static void VariantEnabled() { }
}
