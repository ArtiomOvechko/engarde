using Engarde;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Tests;

public sealed class RuntimeTests
{
    private static ServiceProvider Services(Store store, Action<string, EngardeContext>? missing = null) =>
        new ServiceCollection().AddSingleton(store)
            .AddEngarde(builder => builder.UseFeatureStore<Store>((s, name) => s.Enabled(name))
                .OnFeatureMissing(missing ?? ((name, _) => throw new DisabledFeatureException(name))))
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

    [Fact]
    public void Inline_query_APIs_expose_only_ValueTask_bool_async_methods()
    {
        var defaultQuery = Assert.Single(typeof(IEngarde).GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly));
        Assert.Equal(nameof(IEngarde.IsFeatureEnabledAsync), defaultQuery.Name);
        Assert.Equal(typeof(ValueTask<bool>), defaultQuery.ReturnType);
        Assert.Equal(typeof(string), Assert.Single(defaultQuery.GetParameters()).ParameterType);

        foreach (var type in new[] { typeof(IVariantEngarde), typeof(IEnabledEngarde), typeof(IFixedVariantGuard) })
        {
            var methods = type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly);
            Assert.DoesNotContain(methods, method => method.Name == "IsEnabled");
            var queries = methods.Where(method => method.Name == "IsFeatureEnabledAsync").ToArray();
            Assert.NotEmpty(queries);
            Assert.All(queries, method => Assert.Equal(typeof(ValueTask<bool>), method.ReturnType));
        }
        foreach (var type in new[] { typeof(Engarde.Engarde), typeof(VariantEngarde), typeof(EnabledEngarde), typeof(FixedVariantGuard) })
            Assert.DoesNotContain(type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static),
                method => method.Name == "IsFeatureEnabledAsync");
    }

    [Fact]
    public async Task Inline_query_returns_before_asynchronous_store_finishes()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var services = new ServiceCollection().AddSingleton(new Store())
            .AddEngarde(builder => builder.UseFeatureStore<Store>((_, _) =>
                { calls++; return new ValueTask<bool>(completion.Task); })
                .OnFeatureMissing((_, _) => throw new Exception("Inline queries do not invoke the handler")))
            .BuildServiceProvider();
        using var active = Engarde.Engarde.BeginScope(services);
        ValueTask<bool> first = Engarde.Engarde.CreateInstance().IsFeatureEnabledAsync("pending");
        ValueTask<bool> second = Engarde.Engarde.CreateInstance().IsFeatureEnabledAsync("pending");
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.Equal(2, calls);
        completion.SetResult(true);
        Assert.True(await first);
        Assert.True(await second);
    }

    [Fact]
    public async Task Inline_queries_use_registration_and_return_false_without_failure_handler()
    {
        var store = new Store();
        var failures = 0;
        await using var services = Services(store, (_, _) => failures++);
        using var active = Engarde.Engarde.BeginScope(services);
        Assert.True(await Engarde.Engarde.CreateInstance().IsFeatureEnabledAsync("enabled"));
        Assert.False(await Engarde.Engarde.CreateInstance().IsFeatureEnabledAsync("disabled"));
        Assert.True(await Engarde.Engarde.CreateInstance().IsFeatureEnabledAsync("enabled"));
        Assert.Equal(0, failures);
        await Assert.ThrowsAsync<ArgumentException>(() => Engarde.Engarde.CreateInstance().IsFeatureEnabledAsync(" ").AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(() => Engarde.Engarde.CreateInstance().IsFeatureEnabledAsync((string)null!).AsTask());
    }

    [Fact]
    public async Task Generated_helpers_reuse_custom_implementation_and_constructor_settings()
    {
        var store = new Store();
        await using var services = Services(store);
        using var active = Engarde.Engarde.BeginScope(services);
        Assert.True(await VariantEngarde.CreateInstance().IsFeatureEnabledAsync(FeatureVariant.Primary, "new-experience"));
        Assert.True(await VariantEngarde.CreateInstance().IsFeatureEnabledAsync(FeatureVariant.Primary, "new-experience"));
        Assert.False(await VariantEngarde.CreateInstance().IsFeatureEnabledAsync(FeatureVariant.Secondary, "new-experience"));
        Assert.Equal(3, store.CustomCalls);
        Assert.True(await OverloadedGuard.CreateInstance().IsFeatureEnabledAsync("enabled"));
        Assert.True(await OverloadedGuard.CreateInstance().IsFeatureEnabledAsync(12, "enabled"));
        Assert.True(await Container<int>.NestedGuard.CreateInstance().IsFeatureEnabledAsync("enabled"));
        Guarded.Custom();
        Assert.Equal(4, store.CustomCalls);
    }

    [Fact]
    public async Task Inline_and_woven_checks_call_the_store_every_time()
    {
        var store = new Store();
        await using var services = Services(store);
        using var active = Engarde.Engarde.BeginScope(services);
        Assert.True(await Engarde.Engarde.CreateInstance().IsFeatureEnabledAsync("enabled"));
        Guarded.Enabled();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ => Assert.True(await Engarde.Engarde.CreateInstance().IsFeatureEnabledAsync("enabled"))));
        Assert.Equal(22, store.Calls);
        store.AllowEnabled = false;
        Assert.False(await Engarde.Engarde.CreateInstance().IsFeatureEnabledAsync("enabled"));
        Assert.Throws<DisabledFeatureException>(Guarded.Enabled);
        Assert.Equal(24, store.Calls);
    }

    [Fact]
    public async Task Woven_guards_cover_methods_DTOs_fields_parameters_and_type_constructors()
    {
        await using var services = Services(new Store());
        using var active = Engarde.Engarde.BeginScope(services);
        Assert.Throws<DisabledFeatureException>(Guarded.Disabled);
        Assert.Throws<DisabledFeatureException>(() => new GuardedType());
        Assert.Throws<DisabledFeatureException>(() => Guarded.Parameter("value"));
        Guarded.Parameter("");
        var dto = new GuardedDto();
        dto.Name = "";
        Assert.Throws<DisabledFeatureException>(() => dto.Name = "value");
        Assert.Equal("", dto.Name);
        Assert.Throws<DisabledFeatureException>(() => dto.Field = "value");
        Assert.Null(dto.Field);
        Assert.Throws<DisabledFeatureException>(() => new GuardedDto("value"));
    }

    [Fact]
    public async Task Missing_handler_receives_context_for_each_operation()
    {
        var calls = new List<EngardeContext>();
        await using var services = Services(new Store(), (_, context) => calls.Add(context));
        using var active = Engarde.Engarde.BeginScope(services);
        Assert.False(await Engarde.Engarde.CreateInstance().IsFeatureEnabledAsync("disabled"));
        Guarded.Disabled();
        var dto = new GuardedDto { Name = "value" };
        Assert.Equal(2, calls.Count);
        Assert.Equal(nameof(Guarded.Disabled), calls[0].Member.Name);
        Assert.Equal(nameof(GuardedDto.Name), calls[1].Member.Name);
        Assert.Equal("value", calls[1].Value);
        Assert.Null(dto.Name);
    }

    [Fact]
    public async Task Root_provider_checks_get_their_own_scope_disposed_after_the_check()
    {
        var store = new Store();
        await using var services = new ServiceCollection().AddSingleton(store).AddScoped<ScopeProvider>()
            .AddEngarde(builder => builder.UseFeatureStore<Store, ScopeProvider, int>(
                provider => { provider.Resolutions++; return ValueTask.FromResult(provider.Id); },
                (s, name, id) => { Assert.Equal(7, id); return s.Enabled(name); })
                .OnFeatureMissing((_, _) => { })).BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var active = Engarde.Engarde.BeginScope(services);
        Assert.True(await Engarde.Engarde.CreateInstance().IsFeatureEnabledAsync("enabled"));
        var first = ScopeProvider.Last!;
        Assert.False(await Engarde.Engarde.CreateInstance().IsFeatureEnabledAsync("disabled"));
        var second = ScopeProvider.Last!;
        Assert.NotSame(first, second);
        Assert.All(new[] { first, second }, provider =>
        {
            Assert.Equal(1, provider.Resolutions);
            Assert.True(provider.Disposed);
        });
    }

    [Fact]
    public async Task Custom_scope_ID_overrides_default_scope()
    {
        var scopeIds = new List<int>();
        await using var services = new ServiceCollection().AddSingleton(new Store()).AddSingleton(new ScopeProvider())
            .AddEngarde(builder => builder.UseFeatureStore<Store, ScopeProvider, int>(
                _ => ValueTask.FromResult(7),
                (_, _, id) => { scopeIds.Add(id); return ValueTask.FromResult(id == 7); })
                .OnFeatureMissing((_, _) => { })).BuildServiceProvider();
        using var active = Engarde.Engarde.BeginScope(services);
        Assert.True(await Engarde.Engarde.CreateInstance().IsFeatureEnabledAsync("enabled"));
        Assert.False(await ScopedGuard.CreateInstance().IsFeatureEnabledAsync(8, "enabled"));
        Assert.False(await ScopedGuard.CreateInstance().IsFeatureEnabledAsync(8, "enabled"));
        Assert.Equal(new[] { 7, 8, 8 }, scopeIds);
    }

    [Fact]
    public async Task Woven_checks_complete_with_async_store_and_single_threaded_context()
    {
        await using var services = Services(new Store());
        using var active = Engarde.Engarde.BeginScope(services);
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new RejectingContext());
            Guarded.Enabled();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    [Fact]
    public async Task Async_local_activation_is_isolated_across_parallel_flows()
    {
        async Task Check(bool enabled)
        {
            await using var services = Services(new Store { AllowEnabled = enabled });
            using var active = Engarde.Engarde.BeginScope(services);
            await Task.Yield();
            Assert.Equal(enabled, await Engarde.Engarde.CreateInstance().IsFeatureEnabledAsync("enabled"));
        }
        await Task.WhenAll(Check(true), Check(false));
    }

    [Fact]
    public async Task Fully_overridden_guard_does_not_require_DI_registration()
    {
        var queries = Engarde.Engarde.CreateInstance();
        Assert.True(await queries.IsFeatureEnabledAsync(new StandaloneGuardAttribute(true)));
        await Assert.ThrowsAsync<DisabledFeatureException>(() => queries.IsFeatureEnabledAsync(new StandaloneGuardAttribute(false)).AsTask());
        StandaloneGuarded.Enabled();
        Assert.Throws<DisabledFeatureException>(StandaloneGuarded.Disabled);
    }

    [Fact]
    public async Task Feature_store_resolver_can_return_a_hardcoded_instance_without_DI_registration()
    {
        HardcodedStoreGuardAttribute.FeatureStore.Enabled = true;
        HardcodedStoreGuardAttribute.FeatureStore.Calls = 0;
        var queries = Engarde.Engarde.CreateInstance();
        Assert.True(await queries.IsFeatureEnabledAsync(new HardcodedStoreGuardAttribute()));
        HardcodedStoreGuarded.Run();
        Assert.Equal(2, HardcodedStoreGuardAttribute.FeatureStore.Calls);
    }

    [Fact]
    public async Task Generated_feature_bound_subclasses_use_the_template_implementation()
    {
        PermissionGuard.FeatureStore.Calls = 0;
        IFeature1PermissionGuard first = Feature1PermissionGuard.CreateInstance();
        IFeature2PermissionGuard second = Feature2PermissionGuard.CreateInstance();
        Assert.True(await first.IsFeatureEnabledAsync());
        Assert.True(await second.IsFeatureEnabledAsync());
        GeneratedPermissionGuarded.First();
        GeneratedPermissionGuarded.Second();
        Assert.Equal(4, PermissionGuard.FeatureStore.Calls);
        Assert.True(typeof(Feature1PermissionGuard).IsSealed);
        Assert.True(typeof(Feature2PermissionGuard).IsSealed);
        Assert.Equal(new[] { "feature1" }, new Feature1PermissionGuard().FeatureNames);
        Assert.Equal(new[] { "feature2" }, new Feature2PermissionGuard().FeatureNames);
    }

    private sealed class RejectingContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => throw new InvalidOperationException("Would deadlock");
    }
}

public sealed class Store
{
    public bool AllowEnabled = true;
    public int Calls;
    public int CustomCalls;
    public async ValueTask<bool> Enabled(string name)
    {
        Interlocked.Increment(ref Calls);
        await Task.Yield();
        return AllowEnabled && name == "enabled";
    }
    public async ValueTask<bool> Enabled(FeatureVariant variant, string name)
    {
        Interlocked.Increment(ref CustomCalls);
        await Task.Yield();
        return variant == FeatureVariant.Primary && name == "new-experience";
    }
}

public enum FeatureVariant { Primary, Secondary }

[SkipInlineFailures]
public sealed partial class VariantEngarde(FeatureVariant variant, params string[] featureNames)
    : EngardeAttribute<Store>(featureNames)
{
    protected override ValueTask<bool> IsEnabledAsync(Store store, string featureName) => store.Enabled(variant, featureName);
}

public sealed partial class ScopedGuard(int id, params string[] featureNames)
    : EngardeAttribute<ScopeProvider, int>(featureNames)
{
    protected override ValueTask<int> GetFeatureScopeIdAsync(ScopeProvider provider) => ValueTask.FromResult(id);
}

public partial class OverloadedGuard : EngardeAttribute
{
    public OverloadedGuard(params string[] featureNames) : base(featureNames) { }
    public OverloadedGuard(int number, params string[] featureNames) : base(featureNames) { }
}

public partial class Container<T> where T : struct
{
    public partial class NestedGuard(params string[] featureNames) : EngardeAttribute(featureNames);
}

public sealed class ScopeProvider : IDisposable
{
    public static ScopeProvider? Last;
    public ScopeProvider() => Last = this;
    public int Id => 7;
    public int Resolutions;
    public bool Disposed;
    public void Dispose() => Disposed = true;
}

public sealed class DisabledFeatureException(string name) : Exception(name);

public sealed class StandaloneGuardAttribute(bool enabled) : EngardeAttribute("standalone")
{
    protected override ValueTask<bool> IsEnabledAsync(string featureName, EngardeContext context) =>
        ValueTask.FromResult(enabled);

    protected override void OnFeatureMissing(string featureName, EngardeContext context) =>
        throw new DisabledFeatureException(featureName);
}

public static class StandaloneGuarded
{
    [StandaloneGuard(true)] public static void Enabled() { }
    [StandaloneGuard(false)] public static void Disabled() { }
}

public sealed class HardcodedStore
{
    public bool Enabled;
    public int Calls;

    public ValueTask<bool> Check(string featureName)
    {
        Calls++;
        return ValueTask.FromResult(Enabled && featureName == "hardcoded");
    }
}

public sealed class HardcodedStoreGuardAttribute() : EngardeAttribute<HardcodedStore>("hardcoded")
{
    public static HardcodedStore FeatureStore { get; } = new();

    protected override HardcodedStore ResolveFeatureStore(IServiceProvider services) => FeatureStore;

    protected override ValueTask<bool> IsEnabledAsync(HardcodedStore store, string featureName) =>
        store.Check(featureName);

    protected override void OnFeatureMissing(string featureName, EngardeContext context) =>
        throw new DisabledFeatureException(featureName);
}

public static class HardcodedStoreGuarded
{
    [HardcodedStoreGuard] public static void Run() { }
}

public sealed class PermissionStore
{
    public int Calls;
    public ValueTask<bool> Check(string featureName)
    {
        Calls++;
        return ValueTask.FromResult(featureName is "feature1" or "feature2");
    }
}

[GenerateFeatureGuards("feature1", "feature2")]
public partial class PermissionGuard : EngardeAttribute<PermissionStore>
{
    public static PermissionStore FeatureStore { get; } = new();
    protected override PermissionStore ResolveFeatureStore(IServiceProvider services) => FeatureStore;
    protected override ValueTask<bool> IsEnabledAsync(PermissionStore store, string featureName) =>
        store.Check(featureName);
    protected override void OnFeatureMissing(string featureName, EngardeContext context) =>
        throw new DisabledFeatureException(featureName);
}

public static class GeneratedPermissionGuarded
{
    [Feature1PermissionGuard] public static void First() { }
    [Feature2PermissionGuard] public static void Second() { }
}

public static class Guarded
{
    [Engarde("enabled")] public static void Enabled() { }
    [Engarde("disabled")] public static void Disabled() { }
    [VariantEngarde(FeatureVariant.Primary, "new-experience")] public static void Custom() { }
    public static void Parameter([Engarde("disabled")] string value) { }
}

[Engarde("disabled")]
public sealed class GuardedType;

public sealed class GuardedDto
{
    public GuardedDto() { }
    public GuardedDto(string name) => Name = name;
    [Engarde("disabled")] public string? Name { get; set; }
    [Engarde("disabled")] public string? Field;
}
