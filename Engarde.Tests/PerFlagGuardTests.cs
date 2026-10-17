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

        Assert.True(await EnabledEngarde.CreateInstance().IsFeatureEnabledAsync());
        Assert.True(await EnabledEngarde.CreateInstance().IsFeatureEnabledAsync());
        Assert.True(await DerivedEnabledGuard.CreateInstance().IsFeatureEnabledAsync());
        Assert.True(await Engarde.Engarde.CreateInstance().IsFeatureEnabledAsync(new EnabledEngarde()));
        Assert.True(await Engarde.Engarde.CreateInstance().IsFeatureEnabledAsync(new EnabledEngarde()));
        PerFlagHandlers.Enabled();
        await Assert.ThrowsAsync<DisabledFeatureException>(() => DisabledEngarde.CreateInstance().IsFeatureEnabledAsync().AsTask());
        await Assert.ThrowsAsync<DisabledFeatureException>(() => DisabledEngarde.CreateInstance().IsFeatureEnabledAsync().AsTask());
        Assert.Throws<DisabledFeatureException>(PerFlagHandlers.Disabled);
        Assert.Equal(Enumerable.Repeat(("enabled", 7), 6).Concat(Enumerable.Repeat(("disabled", 7), 3)), calls);
        Assert.Equal(9, resolutions);
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
        Assert.True(await SelectedFlagGuard.CreateInstance().IsFeatureEnabledAsync(true));
        Assert.False(await SelectedFlagGuard.CreateInstance().IsFeatureEnabledAsync(false));
        Assert.True(await FixedScopeGuard.CreateInstance().IsFeatureEnabledAsync(7));
        Assert.False(await FixedScopeGuard.CreateInstance().IsFeatureEnabledAsync(8));
        Assert.True(await NamedScopeFlagGuard.CreateInstance().IsFeatureEnabledAsync("default"));
        Assert.False(await NamedScopeFlagGuard.CreateInstance().IsFeatureEnabledAsync("other"));
        Assert.Throws<DisabledFeatureException>(PerFlagHandlers.Scoped);
        Assert.Equal(new[] { 7, 7, 7, 8, 7, 8, 8 }, scopes);
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
        Assert.True(await FixedVariantGuard.CreateInstance().IsFeatureEnabledAsync(FeatureVariant.Primary));
        Assert.True(await FixedVariantGuard.CreateInstance().IsFeatureEnabledAsync(FeatureVariant.Primary));
        Assert.False(await FixedVariantGuard.CreateInstance().IsFeatureEnabledAsync(FeatureVariant.Secondary));
        PerFlagHandlers.VariantEnabled();
        Assert.Equal(4, store.CustomCalls);

        Assert.True(await FixedStoreScopeGuard.CreateInstance().IsFeatureEnabledAsync(FeatureVariant.Primary));
        Assert.True(await FixedStoreScopeGuard.CreateInstance().IsFeatureEnabledAsync(FeatureVariant.Primary));
        Assert.False(await FixedStoreScopeGuard.CreateInstance().IsFeatureEnabledAsync(FeatureVariant.Secondary));
        Assert.Equal(7, store.CustomCalls);
        Assert.Equal(3, scopeProvider.Resolutions);
    }

    [Fact]
    public async Task Encapsulated_multiple_flags_use_all_of_semantics_without_invoking_failure_handler()
    {
        var store = new Store();
        await using var services = new ServiceCollection().AddSingleton(store)
            .AddEngarde(builder => builder.UseFeatureStore<Store>((s, flag) => s.Enabled(flag))
                .OnFeatureMissing((_, _) => throw new Exception("Inline checks must not invoke handler"))).BuildServiceProvider();
        using var active = Engarde.Engarde.BeginScope(services);
        Assert.False(await CombinedEngarde.CreateInstance().IsFeatureEnabledAsync());
        Assert.False(await CombinedEngarde.CreateInstance().IsFeatureEnabledAsync());
        Assert.Equal(4, store.Calls);
        await Assert.ThrowsAsync<ArgumentNullException>(() => Engarde.Engarde.CreateInstance().IsFeatureEnabledAsync((EngardeAttribute)null!).AsTask());
    }
}

public sealed partial class EnabledEngarde() : EngardeAttribute("enabled");
public sealed partial class DisabledEngarde() : EngardeAttribute("disabled");
public abstract class BaseEnabledGuard() : EngardeAttribute("enabled");
public sealed partial class DerivedEnabledGuard() : BaseEnabledGuard;
[SkipInlineFailures]
public sealed partial class CombinedEngarde() : EngardeAttribute("disabled", "enabled");
[SkipInlineFailures]
public sealed partial class SelectedFlagGuard(bool enabled) : EngardeAttribute(enabled ? "enabled" : "disabled");

[SkipInlineFailures]
public sealed partial class FixedScopeGuard(int id) : EngardeAttribute<ScopeProvider, int>("enabled")
{
    protected override ValueTask<int> GetFeatureScopeIdAsync(ScopeProvider provider) => ValueTask.FromResult(id);
}

[SkipInlineFailures]
public sealed partial class NamedScopeFlagGuard(string featurePool) : EngardeAttribute<ScopeProvider, int>("enabled")
{
    protected override ValueTask<int> GetFeatureScopeIdAsync(ScopeProvider provider) => ValueTask.FromResult(featurePool == "default" ? 7 : 8);
}

[SkipInlineFailures]
public sealed partial class FixedVariantGuard(FeatureVariant variant) : EngardeAttribute<Store>("new-experience")
{
    protected override ValueTask<bool> IsEnabledAsync(Store store, string featureName) => store.Enabled(variant, featureName);
}

[SkipInlineFailures]
public sealed partial class FixedStoreScopeGuard(FeatureVariant variant) : EngardeAttribute<Store, ScopeProvider, FeatureVariant>("new-experience")
{
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
