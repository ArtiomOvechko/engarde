using Engarde;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Guard = Engarde.Engarde;

namespace Tests;

public sealed class InlineQueryTests
{
    private static ServiceProvider Services(List<(string Flag, int Scope)> calls, List<string> missing) => new ServiceCollection()
        .AddSingleton(new Store()).AddScoped<ScopeProvider>()
        .AddEngarde(builder => builder.UseFeatureStore<Store, ScopeProvider, int>(
                (_, flag, scope) => { calls.Add((flag, scope)); return ValueTask.FromResult(flag == "enabled" && scope == 7); },
                provider => ValueTask.FromResult(provider.Id))
            .OnFeatureMissing((flag, _) => missing.Add(flag)))
        .BuildServiceProvider();

    [Fact]
    public async Task Generated_scope_overloads_use_the_supplied_scope_id_instead_of_resolving_one()
    {
        var calls = new List<(string Flag, int Scope)>();
        await using var services = Services(calls, []);
        using var active = Guard.BeginScope(services);

        Assert.True(await FixedScopeGuard.CreateInstance().IsFeatureEnabledAsync(8, 7));
        Assert.False(await FixedScopeGuard.CreateInstance().IsFeatureEnabledAsync(7, 8));
        Assert.True(await ScopedGuard.CreateInstance().IsFeatureEnabledAsync(8, "enabled", 7));
        Assert.Equal(new[] { ("enabled", 7), ("enabled", 8), ("enabled", 7) }, calls);
    }

    [Fact]
    public async Task Generated_scope_overloads_skip_resolution_for_guards_with_their_own_store()
    {
        var store = new Store();
        var scopeProvider = new ScopeProvider();
        await using var services = new ServiceCollection().AddSingleton(store).AddSingleton(scopeProvider)
            .AddEngarde(builder => builder.UseFeatureStore<Store>((_, _) => throw new Exception("Default store must be overridden"))
                .OnFeatureMissing((flag, _) => throw new DisabledFeatureException(flag))).BuildServiceProvider();
        using var active = Guard.BeginScope(services);

        Assert.True(await FixedStoreScopeGuard.CreateInstance().IsFeatureEnabledAsync(FeatureVariant.Secondary, FeatureVariant.Primary));
        Assert.False(await FixedStoreScopeGuard.CreateInstance().IsFeatureEnabledAsync(FeatureVariant.Primary, FeatureVariant.Secondary));
        Assert.Equal(0, scopeProvider.Resolutions);
    }

    [Fact]
    public async Task Guard_queries_accept_a_scope_id_for_the_registered_store_and_validate_its_type()
    {
        var calls = new List<(string Flag, int Scope)>();
        var missing = new List<string>();
        await using var services = Services(calls, missing);
        using var active = Guard.BeginScope(services);
        var queries = Guard.CreateInstance();

        Assert.False(await queries.IsFeatureEnabledAsync(new EnabledEngarde(), 8));
        Assert.True(await queries.IsFeatureEnabledAsync(new CombinedEngarde(), "enabled", 7));
        Assert.Equal(new[] { ("enabled", 8), ("enabled", 7) }, calls);
        Assert.Equal(new[] { "enabled" }, missing);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queries.IsFeatureEnabledAsync(new EnabledEngarde(), 7L).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => queries.IsFeatureEnabledAsync<string>(new EnabledEngarde(), "7").AsTask());
    }

    [Fact]
    public async Task Default_queries_accept_a_scope_id_for_the_registered_store_and_validate_its_type()
    {
        var calls = new List<(string Flag, int Scope)>();
        var missing = new List<string>();
        await using var services = Services(calls, missing);
        using var active = Guard.BeginScope(services);
        IEngarde queries = Guard.CreateInstance();

        Assert.True(await queries.IsFeatureEnabledAsync("enabled", 7));
        Assert.False(await queries.IsFeatureEnabledAsync("enabled", 8));
        Assert.Equal(new[] { ("enabled", 7), ("enabled", 8) }, calls);
        Assert.Equal(new[] { "enabled" }, missing);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            queries.IsFeatureEnabledAsync("enabled", 7L).AsTask());
    }

    [Fact]
    public async Task Global_skip_inline_failures_applies_to_default_and_custom_queries()
    {
        await using var services = new ServiceCollection().AddSingleton(new Store()).AddScoped<ScopeProvider>()
            .AddEngarde(builder =>
            {
                builder.UseFeatureStore<Store, ScopeProvider, int>(
                    (_, _, _) => ValueTask.FromResult(false), _ => ValueTask.FromResult(7));
                builder.OnFeatureMissing((_, _) => throw new DisabledFeatureException("failure handler"));
                builder.SkipInlineFailures = true;
            }).BuildServiceProvider();
        using var active = Guard.BeginScope(services);

        Assert.False(await Guard.CreateInstance().IsFeatureEnabledAsync("disabled"));
        Assert.False(await DisabledEngarde.CreateInstance().IsFeatureEnabledAsync());
    }

    [Fact]
    public async Task Feature_bound_guards_on_the_registered_store_accept_a_scope_id()
    {
        var calls = new List<(string Flag, int Scope)>();
        await using var services = Services(calls, []);
        using var active = Guard.BeginScope(services);

        Assert.True(await EnabledRegisteredScopeGuard.CreateInstance().IsFeatureEnabledAsync(7));
        Assert.False(await EnabledRegisteredScopeGuard.CreateInstance().IsFeatureEnabledAsync(8));
        Assert.Equal(new[] { ("enabled", 7), ("enabled", 8) }, calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EnabledRegisteredScopeGuard.CreateInstance().IsFeatureEnabledAsync(7L).AsTask());
    }

    [Fact]
    public async Task Query_cancellation_tokens_reach_guards_and_cancelled_queries_throw()
    {
        await using var services = Services([], []);
        using var active = Guard.BeginScope(services);
        using var source = new CancellationTokenSource();
        TokenGuard.Seen.Value = [];

        Assert.True(await TokenGuard.CreateInstance().IsFeatureEnabledAsync(source.Token));
        Assert.Equal(new[] { source.Token, source.Token }, TokenGuard.Seen.Value);

        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TokenGuard.CreateInstance().IsFeatureEnabledAsync(source.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Guard.CreateInstance().IsFeatureEnabledAsync("enabled", source.Token).AsTask());
    }

    [Fact]
    public async Task Scope_ids_are_rejected_when_the_store_has_no_feature_scope()
    {
        await using var services = new ServiceCollection().AddSingleton(new Store())
            .AddEngarde(builder => builder.UseFeatureStore<Store>((s, flag) => s.Enabled(flag))
                .OnFeatureMissing((_, _) => { })).BuildServiceProvider();
        using var active = Guard.BeginScope(services);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Guard.CreateInstance().IsFeatureEnabledAsync(new EnabledEngarde(), 7).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Guard.CreateInstance().IsFeatureEnabledAsync("enabled", 7).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Guard.CreateInstance().IsFeatureEnabledAsync(new FixedVariantGuard(FeatureVariant.Primary), 7).AsTask());
    }

    [Fact]
    public async Task Skipping_inline_failures_is_inherited_by_feature_bound_guards_but_not_by_woven_checks()
    {
        var missing = new List<string>();
        await using var services = Services([], missing);
        using var active = Guard.BeginScope(services);

        Assert.False(await DisabledQuietGuard.CreateInstance().IsFeatureEnabledAsync());
        Assert.Empty(missing);
        QuietGuarded.Run();
        Assert.Equal(new[] { "disabled" }, missing);
        Assert.False(await DisabledEngarde.CreateInstance().IsFeatureEnabledAsync());
        Assert.Equal(new[] { "disabled", "disabled" }, missing);
    }
}

[SkipInlineFailures]
[GenerateFeatureGuards("disabled")]
public partial class QuietGuard : EngardeAttribute;

[GenerateFeatureGuards("enabled")]
public partial class RegisteredScopeGuard : EngardeAttribute;

public static class QuietGuarded
{
    [DisabledQuietGuard] public static void Run() { }
}

public sealed partial class TokenGuard() : EngardeAttribute<Store, ScopeProvider, int>("enabled")
{
    public static readonly AsyncLocal<List<CancellationToken>> Seen = new();

    protected override ValueTask<int> GetScopeIdAsync(ScopeProvider provider, CancellationToken cancellationToken)
    {
        Seen.Value?.Add(cancellationToken);
        return ValueTask.FromResult(7);
    }

    protected override ValueTask<bool> IsEnabledAsync(Store store, string featureName, int scopeId, CancellationToken cancellationToken)
    {
        Seen.Value?.Add(cancellationToken);
        return ValueTask.FromResult(true);
    }
}
