using Engarde;
using Engarde.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using Xunit;

namespace Tests;

public sealed class EngardeTestingTests
{
    [Fact]
    public async Task Disable_all_is_inherited_by_derived_test_classes_and_flows_through_async_calls()
    {
        var subject = new DisabledDerivedTest();

        subject.Run();
        await subject.RunAsync();
    }

    [Fact]
    public void Enable_all_restores_default_guard_behavior_below_a_disabled_base_class()
    {
        Assert.Throws<FirstTestingGuardException>(() => new EnabledDerivedTest().Run());
        Assert.Throws<FirstTestingGuardException>(() => new MethodEnabledTest().Run());
    }

    [Fact]
    public void Generated_specific_attributes_override_only_the_selected_guard_type()
    {
        var subject = new FirstDisabledTest();

        subject.First();
        Assert.Throws<SecondTestingGuardException>(subject.Second);
    }

    [Fact]
    public void Generated_specific_enable_overrides_disable_all()
    {
        var subject = new FirstEnabledTest();

        Assert.Throws<FirstTestingGuardException>(subject.First);
        subject.Second();
    }

    [Fact]
    public void Default_and_custom_override_attributes_are_available()
    {
        Assert.Equal("EnableEngardeAttribute", typeof(EnableEngardeAttribute).Name);
        Assert.Equal("DisableEngardeAttribute", typeof(DisableEngardeAttribute).Name);
        Assert.Equal("EnableTestingFirstGuardAttribute", typeof(EnableTestingFirstGuardAttribute).Name);
        Assert.Equal("DisableITestingFirstGuardAttribute", typeof(DisableITestingFirstGuardAttribute).Name);
    }

    [Fact]
    public async Task Default_Engarde_can_be_disabled_or_re_enabled_independently()
    {
        new DefaultDisabledTest().Run();

        await using var services = new ServiceCollection().AddSingleton(new TestingStore())
            .AddEngarde(builder => builder.UseFeatureStore<TestingStore>((_, _) => ValueTask.FromResult(false))
                .OnFeatureMissing((_, _) => throw new DefaultTestingGuardException()))
            .BuildServiceProvider();
        using var active = Engarde.Engarde.BeginScope(services);
        var enabled = new DefaultEnabledTest();
        Assert.Throws<DefaultTestingGuardException>(enabled.Default);
        enabled.Custom();
    }

    [Fact]
    public void Override_scope_is_restored_when_an_annotated_method_throws()
    {
        Assert.Throws<InvalidOperationException>(() => new ThrowingDisabledTest().Run());
        Assert.Throws<FirstTestingGuardException>(TestingGuardedCode.First);
    }

    [Fact]
    public void Named_feature_filters_apply_per_flag_for_all_and_specific_overrides()
    {
        TestingSelectableGuardAttribute.Evaluated.Clear();
        new NamedAllEnabledTest().Run();
        Assert.Equal(new[] { "weather-feature" }, TestingSelectableGuardAttribute.Evaluated);

        TestingSelectableGuardAttribute.Evaluated.Clear();
        new NamedSpecificDisabledTest().Run();
        Assert.Equal(new[] { "new-ui-experience" }, TestingSelectableGuardAttribute.Evaluated);
    }

    [DisableAllEngarde]
    private abstract class DisabledBaseTest;

    private sealed class DisabledDerivedTest : DisabledBaseTest
    {
        public void Run() => TestingGuardedCode.First();

        public async Task RunAsync()
        {
            await Task.Yield();
            await TestingGuardedCode.FirstAsync();
        }
    }

    [EnableAllEngarde]
    private sealed class EnabledDerivedTest : DisabledBaseTest
    {
        public void Run() => TestingGuardedCode.First();
    }

    [DisableAllEngarde]
    private sealed class MethodEnabledTest
    {
        [EnableAllEngarde]
        public void Run() => TestingGuardedCode.First();
    }

    [DisableITestingFirstGuard]
    private sealed class FirstDisabledTest
    {
        public void First() => TestingGuardedCode.First();
        public void Second() => TestingGuardedCode.Second();
    }

    [DisableAllEngarde, EnableTestingFirstGuard]
    private sealed class FirstEnabledTest
    {
        public void First() => TestingGuardedCode.First();
        public void Second() => TestingGuardedCode.Second();
    }

    [DisableEngarde]
    private sealed class DefaultDisabledTest
    {
        public void Run() => TestingGuardedCode.Default();
    }

    [DisableAllEngarde, EnableEngarde]
    private sealed class DefaultEnabledTest
    {
        public void Default() => TestingGuardedCode.Default();
        public void Custom() => TestingGuardedCode.First();
    }

    private sealed class TestingStore;

    [DisableAllEngarde]
    private sealed class ThrowingDisabledTest
    {
        public void Run() => throw new InvalidOperationException();
    }

    [DisableAllEngarde, EnableAllEngarde("weather-feature")]
    private sealed class NamedAllEnabledTest
    {
        public void Run() => TestingGuardedCode.Selectable();
    }

    [DisableTestingSelectableGuard("weather-feature")]
    private sealed class NamedSpecificDisabledTest
    {
        public void Run() => TestingGuardedCode.Selectable();
    }
}

public sealed class TestingFirstGuardAttribute : EngardeAttribute
{
    public TestingFirstGuardAttribute() : base("first") { }

    protected override ValueTask<bool> IsEnabledAsync(string featureName, EngardeContext context) =>
        ValueTask.FromResult(false);

    protected override void OnFeatureMissing(string featureName, EngardeContext context) =>
        throw new FirstTestingGuardException();
}

public sealed class TestingSecondGuardAttribute : EngardeAttribute
{
    public TestingSecondGuardAttribute() : base("second") { }

    protected override ValueTask<bool> IsEnabledAsync(string featureName, EngardeContext context) =>
        ValueTask.FromResult(false);

    protected override void OnFeatureMissing(string featureName, EngardeContext context) =>
        throw new SecondTestingGuardException();
}

public sealed class TestingSelectableGuardAttribute(params string[] featureNames) : EngardeAttribute(featureNames)
{
    public static ConcurrentQueue<string> Evaluated { get; } = new();

    protected override ValueTask<bool> IsEnabledAsync(string featureName, EngardeContext context)
    {
        Evaluated.Enqueue(featureName);
        return ValueTask.FromResult(true);
    }
}

public sealed class FirstTestingGuardException : Exception;
public sealed class SecondTestingGuardException : Exception;
public sealed class DefaultTestingGuardException : Exception;

public static class TestingGuardedCode
{
    [TestingFirstGuard] public static void First() { }
    [TestingSecondGuard] public static void Second() { }
    [TestingFirstGuard] public static async Task FirstAsync() => await Task.Yield();
    [Engarde("default")] public static void Default() { }
    [TestingSelectableGuard("weather-feature", "new-ui-experience")] public static void Selectable() { }
}
