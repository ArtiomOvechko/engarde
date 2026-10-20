using Engarde;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Engarde.Tests;

public sealed class FallbackTests
{
    private static ServiceProvider Provider(bool enabled, params ServiceDescriptor[] services)
    {
        var collection = new ServiceCollection();
        foreach (var service in services) ((ICollection<ServiceDescriptor>)collection).Add(service);
        collection.AddEngarde(builder => builder
            .UseFeatureStore<FallbackStore>((store, _) => ValueTask.FromResult(store.Enabled))
            .OnFeatureMissing((_, _) => throw new FallbackDisabledException()));
        collection.AddSingleton(new FallbackStore(enabled));
        return collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    [Fact]
    public void Disabled_instance_method_uses_same_instance_fallback()
    {
        using var provider = Provider(false);
        using var active = global::Engarde.Engarde.BeginScope(provider);
        var subject = new MethodFallbackSubject();

        Assert.Equal("fallback:4", subject.Read(4));
        Assert.Equal("static-fallback:4", subject.ReadWithStaticFallback(4));
        Assert.Equal(0, subject.PrimaryCalls);
    }

    [Fact]
    public void Enabled_instance_method_uses_primary_method()
    {
        using var provider = Provider(true);
        using var active = global::Engarde.Engarde.BeginScope(provider);
        var subject = new MethodFallbackSubject();

        Assert.Equal("primary:4", subject.Read(4));
        Assert.Equal(1, subject.PrimaryCalls);
    }

    [Fact]
    public void Disabled_static_method_uses_static_fallback()
    {
        using var provider = Provider(false);
        using var active = global::Engarde.Engarde.BeginScope(provider);

        Assert.Equal(12, StaticFallbackSubject.Calculate(4));
    }

    [Fact]
    public async Task Disabled_async_method_uses_async_fallback_without_blocking_dispatch()
    {
        using var provider = Provider(false);
        using var active = global::Engarde.Engarde.BeginScope(provider);

        Assert.Equal("fallback:5", await AsyncFallbackSubject.ReadAsync(5));
    }

    [Fact]
    public void Class_fallback_is_resolved_from_the_active_DI_scope()
    {
        using var provider = Provider(false,
            ServiceDescriptor.Scoped(typeof(ClassPrimaryService), typeof(ClassPrimaryService)),
            ServiceDescriptor.Scoped(typeof(ClassFallbackService), typeof(ClassFallbackService)));
        using var scope = provider.CreateScope();
        using var active = global::Engarde.Engarde.BeginScope(scope.ServiceProvider, false);

        Assert.IsType<ClassFallbackService>(scope.ServiceProvider.GetRequiredService<ClassPrimaryService>());
        Assert.Equal("fallback:6", scope.ServiceProvider.GetRequiredService<ClassPrimaryService>().Read(6));
    }

    [Fact]
    public void Interface_fallback_is_resolved_from_the_active_DI_scope()
    {
        using var provider = Provider(false,
            ServiceDescriptor.Scoped(typeof(IFallbackService), typeof(InterfacePrimaryService)),
            ServiceDescriptor.Scoped(typeof(InterfaceFallbackService), typeof(InterfaceFallbackService)));
        using var scope = provider.CreateScope();
        using var active = global::Engarde.Engarde.BeginScope(scope.ServiceProvider, false);
        var service = scope.ServiceProvider.GetRequiredService<IFallbackService>();

        Assert.IsType<InterfaceFallbackService>(service);
        Assert.Equal("fallback:7", service.Read(7));
    }

    [Fact]
    public void Generated_custom_guard_fallback_constructor_works_without_DI_for_same_instance_method()
    {
        var subject = new GeneratedGuardFallbackSubject();

        Assert.Equal("fallback:8", subject.Read(8));
    }
}

public sealed record FallbackStore(bool Enabled);
public sealed class FallbackDisabledException : Exception;

public sealed class MethodFallbackSubject
{
    public int PrimaryCalls { get; private set; }

    [Engarde(nameof(ReadFallback), typeof(MethodFallbackSubject), "fallback")]
    public string Read(int value)
    {
        PrimaryCalls++;
        return $"primary:{value}";
    }

    private string ReadFallback(int value) => $"fallback:{value}";

    [Engarde(nameof(MethodFallbackStatics.Read), typeof(MethodFallbackStatics), "fallback")]
    public string ReadWithStaticFallback(int value)
    {
        PrimaryCalls++;
        return $"primary:{value}";
    }
}

public static class MethodFallbackStatics
{
    public static string Read(int value) => $"static-fallback:{value}";
}

public static class StaticFallbackSubject
{
    [Engarde(nameof(StaticFallbacks.Calculate), typeof(StaticFallbacks), "fallback")]
    public static int Calculate(int value) => value;
}

public static class StaticFallbacks
{
    public static int Calculate(int value) => value * 3;
}

public static class AsyncFallbackSubject
{
    [Engarde(nameof(ReadFallbackAsync), typeof(AsyncFallbackSubject), "fallback")]
    public static async Task<string> ReadAsync(int value)
    {
        await Task.Yield();
        return $"primary:{value}";
    }

    private static async Task<string> ReadFallbackAsync(int value)
    {
        await Task.Yield();
        return $"fallback:{value}";
    }
}

[Engarde(typeof(ClassFallbackService), "fallback")]
public class ClassPrimaryService
{
    public virtual string Read(int value) => $"primary:{value}";
}

public sealed class ClassFallbackService : ClassPrimaryService
{
    public override string Read(int value) => $"fallback:{value}";
}

[Engarde(typeof(InterfaceFallbackService), "fallback")]
public interface IFallbackService
{
    string Read(int value);
}

public sealed class InterfacePrimaryService : IFallbackService
{
    public string Read(int value) => $"primary:{value}";
}

public sealed class InterfaceFallbackService : IFallbackService
{
    public string Read(int value) => $"fallback:{value}";
}

public sealed partial class AlwaysFallbackGuardAttribute(params string[] featureNames) : EngardeAttribute(featureNames)
{
    protected override ValueTask<bool> IsEnabledAsync(
        string featureName, EngardeContext context, CancellationToken cancellationToken) => ValueTask.FromResult(false);

    protected override void OnFeatureMissing(string featureName, EngardeContext context) { }
}

public sealed class GeneratedGuardFallbackSubject
{
    [AlwaysFallbackGuard(nameof(ReadFallback), typeof(GeneratedGuardFallbackSubject), "fallback")]
    public string Read(int value) => $"primary:{value}";

    private string ReadFallback(int value) => $"fallback:{value}";
}
