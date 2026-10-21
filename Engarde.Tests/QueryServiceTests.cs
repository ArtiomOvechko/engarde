using System.Collections.Concurrent;
using Engarde;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Guard = Engarde.Engarde;

namespace Tests;

public sealed class QueryServiceTests
{
    [Fact]
    public async Task Core_and_generated_interfaces_are_scoped_by_default_and_use_the_resolving_scope()
    {
        await using var services = Services();
        await using var first = services.CreateAsyncScope();
        await using var second = services.CreateAsyncScope();
        var firstQueries = first.ServiceProvider.GetRequiredService<IEngarde>();
        var firstCustom = first.ServiceProvider.GetRequiredService<IInjectedFlagGuard>();
        Assert.Same(firstQueries, first.ServiceProvider.GetRequiredService<IEngarde>());
        Assert.Same(firstCustom, first.ServiceProvider.GetRequiredService<IInjectedFlagGuard>());
        Assert.NotSame(firstQueries, second.ServiceProvider.GetRequiredService<IEngarde>());
        Assert.NotSame(firstCustom, second.ServiceProvider.GetRequiredService<IInjectedFlagGuard>());

        var firstStore = first.ServiceProvider.GetRequiredService<QueryStore>();
        var secondStore = second.ServiceProvider.GetRequiredService<QueryStore>();
        firstStore.Enabled = true;
        secondStore.Enabled = false;
        Assert.True(await firstQueries.IsFeatureEnabledAsync("enabled"));
        Assert.True(await firstCustom.IsFeatureEnabledAsync());
        Assert.False(await second.ServiceProvider.GetRequiredService<IEngarde>().IsFeatureEnabledAsync("enabled"));
        Assert.False(await second.ServiceProvider.GetRequiredService<IInjectedFlagGuard>().IsFeatureEnabledAsync());
        Assert.Equal(2, firstStore.Calls);
        Assert.Equal(2, secondStore.Calls);
        Assert.False(firstStore.Disposed);
        Assert.False(secondStore.Disposed);
    }

    [Fact]
    public async Task Generated_feature_bound_interface_is_automatically_registered_as_scoped()
    {
        await using var services = new ServiceCollection().AddScoped<QueryStore>()
            .AddEngarde(builder => builder.UseFeatureStore<QueryStore>((store, flag) => store.Check(flag))
                .OnFeatureMissing((_, _) => throw new Exception("Queries do not invoke handlers")))
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        await using var scope = services.CreateAsyncScope();
        var first = scope.ServiceProvider.GetRequiredService<IFeature1PermissionGuard>();
        Assert.Same(first, scope.ServiceProvider.GetRequiredService<IFeature1PermissionGuard>());
        Assert.True(await first.IsFeatureEnabledAsync());
    }

    [Theory]
    [InlineData(ServiceLifetime.Singleton)]
    [InlineData(ServiceLifetime.Scoped)]
    [InlineData(ServiceLifetime.Transient)]
    public async Task Selected_lifetimes_apply_to_core_and_generated_query_services(ServiceLifetime lifetime)
    {
        await using var services = Services(lifetime);
        await using var first = services.CreateAsyncScope();
        await using var second = services.CreateAsyncScope();
        foreach (var type in new[] { typeof(IEngarde), typeof(IInjectedFlagGuard) })
        {
            var a = first.ServiceProvider.GetRequiredService(type);
            var b = first.ServiceProvider.GetRequiredService(type);
            var c = second.ServiceProvider.GetRequiredService(type);
            if (lifetime == ServiceLifetime.Transient) Assert.NotSame(a, b);
            else Assert.Same(a, b);
            if (lifetime == ServiceLifetime.Singleton) Assert.Same(a, c);
            else Assert.NotSame(a, c);
        }
    }

    [Fact]
    public async Task Custom_lifetime_override_is_independent_of_the_core_lifetime()
    {
        await using var services = new ServiceCollection().AddScoped<QueryStore>()
            .AddTransient<IInjectedFlagGuard, CustomInjectedFlagGuard>()
            .AddEngarde(builder => builder.UseFeatureStore<QueryStore>((store, flag) => store.Check(flag))
                .OnFeatureMissing((_, _) => throw new Exception("Queries do not invoke handlers"))
                .UseQueryLifetime(ServiceLifetime.Singleton))
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = services.CreateAsyncScope();
        Assert.Same(services.GetRequiredService<IEngarde>(), scope.ServiceProvider.GetRequiredService<IEngarde>());
        Assert.NotSame(scope.ServiceProvider.GetRequiredService<IInjectedFlagGuard>(), scope.ServiceProvider.GetRequiredService<IInjectedFlagGuard>());
    }

    [Fact]
    public async Task Singleton_queries_create_and_dispose_temporary_scopes_outside_requests()
    {
        var observations = new ConcurrentQueue<QueryStore>();
        await using var services = Services(ServiceLifetime.Singleton, observations);
        var queries = services.GetRequiredService<IEngarde>();
        var custom = services.GetRequiredService<IInjectedFlagGuard>();
        Assert.True(await queries.IsFeatureEnabledAsync("enabled"));
        Assert.True(await queries.IsFeatureEnabledAsync("enabled"));
        Assert.True(await custom.IsFeatureEnabledAsync());
        Assert.Equal(3, observations.Count);
        Assert.Equal(3, observations.Distinct().Count());
        Assert.All(observations, store => Assert.True(store.Disposed));
    }

    [Fact]
    public async Task Singleton_queries_reuse_each_requests_services_without_retaining_them()
    {
        var observations = new ConcurrentQueue<QueryStore>();
        await using var services = Services(ServiceLifetime.Singleton, observations);
        var queries = services.GetRequiredService<IEngarde>();
        var custom = services.GetRequiredService<IInjectedFlagGuard>();
        foreach (var enabled in new[] { true, false })
        {
            await using var request = services.CreateAsyncScope();
            var store = request.ServiceProvider.GetRequiredService<QueryStore>();
            store.Enabled = enabled;
            using (Guard.BeginScope(request.ServiceProvider, createScope: false))
            {
                Assert.Equal(enabled, await queries.IsFeatureEnabledAsync("enabled"));
                Assert.Equal(enabled, await custom.IsFeatureEnabledAsync());
                Assert.Equal(enabled, await queries.IsFeatureEnabledAsync("enabled"));
                Assert.Equal(enabled, await custom.IsFeatureEnabledAsync());
                Assert.Equal(4, store.Calls);
                Assert.False(store.Disposed);
            }
            Assert.False(store.Disposed);
        }
        Assert.Equal(8, observations.Count);
        Assert.All(observations, store => Assert.True(store.Disposed));
    }

    [Fact]
    public async Task Provider_bound_factories_work_without_global_activation_and_do_not_dispose_borrowed_services()
    {
        await using var services = Services();
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<QueryStore>();
        var core = Guard.CreateInstance(scope.ServiceProvider);
        var custom = InjectedFlagGuard.CreateInstance(scope.ServiceProvider);
        Assert.NotSame(core, Guard.CreateInstance(scope.ServiceProvider));
        Assert.NotSame(custom, InjectedFlagGuard.CreateInstance(scope.ServiceProvider));
        Assert.True(await core.IsFeatureEnabledAsync("enabled"));
        Assert.True(await custom.IsFeatureEnabledAsync());
        Assert.Equal(2, store.Calls);
        Assert.False(store.Disposed);
    }

    [Fact]
    public async Task Parameterless_factories_create_instances_for_legacy_service_locator_factories()
    {
        var queries = Guard.CreateInstance();
        var custom = InjectedFlagGuard.CreateInstance();
        Assert.NotSame(queries, Guard.CreateInstance());
        Assert.NotSame(custom, InjectedFlagGuard.CreateInstance());
        await using var services = Services();
        using var active = Guard.BeginScope(services);
        Assert.True(await queries.IsFeatureEnabledAsync("enabled"));
        Assert.True(await custom.IsFeatureEnabledAsync());
    }

    [Fact]
    public async Task Bound_query_services_do_not_use_another_containers_active_scope()
    {
        await using var first = Services();
        await using var second = Services();
        await using var firstScope = first.CreateAsyncScope();
        await using var secondScope = second.CreateAsyncScope();
        secondScope.ServiceProvider.GetRequiredService<QueryStore>().Enabled = false;
        using var active = Guard.BeginScope(firstScope.ServiceProvider, createScope: false);
        Assert.True(await firstScope.ServiceProvider.GetRequiredService<IEngarde>().IsFeatureEnabledAsync("enabled"));
        Assert.False(await secondScope.ServiceProvider.GetRequiredService<IEngarde>().IsFeatureEnabledAsync("enabled"));
        Assert.False(await secondScope.ServiceProvider.GetRequiredService<IInjectedFlagGuard>().IsFeatureEnabledAsync());
    }

    [Fact]
    public void Invalid_lifetimes_are_rejected_during_registration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddEngarde(builder => builder.UseQueryLifetime((ServiceLifetime)99)));
    }

    private static ServiceProvider Services(ServiceLifetime lifetime = ServiceLifetime.Scoped, ConcurrentQueue<QueryStore>? observations = null) =>
        new ServiceCollection().AddScoped(_ => new QueryStore(observations))
            .AddEngarde(builder =>
            {
                builder.UseFeatureStore<QueryStore>((store, flag) => store.Check(flag))
                    .OnFeatureMissing((_, _) => throw new Exception("Inline failures are globally skipped"))
                    .UseQueryLifetime(lifetime);
                builder.SkipInlineFailures = true;
            })
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
}

public sealed class QueryStore(ConcurrentQueue<QueryStore>? observations = null) : IAsyncDisposable
{
    public bool Enabled = true;
    public bool Disposed;
    public int Calls;
    public ValueTask<bool> Check(string flag)
    {
        Assert.False(Disposed);
        Calls++;
        observations?.Enqueue(this);
        return ValueTask.FromResult(Enabled && flag == "enabled");
    }
    public ValueTask DisposeAsync() { Disposed = true; return default; }
}

[SkipInlineFailures]
public sealed partial class InjectedFlagGuard() : EngardeAttribute<QueryStore>("enabled")
{
    protected override ValueTask<bool> IsEnabledAsync(QueryStore store, string featureName, CancellationToken cancellationToken) => store.Check(featureName);
}

public sealed class CustomInjectedFlagGuard : IInjectedFlagGuard
{
    public ValueTask<bool> IsFeatureEnabledAsync() => ValueTask.FromResult(true);
    public ValueTask<bool> IsFeatureEnabledAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);
}
