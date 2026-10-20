using Engarde;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Tests;

public sealed class GuardedValueTests
{
    [Fact]
    public void Selective_property_guard_checks_only_its_configured_value()
    {
        var store = new ValueStore();
        using var provider = new ServiceCollection().AddSingleton(store)
            .AddEngarde(builder => builder.UseFeatureStore<ValueStore>((valueStore, feature) =>
            {
                valueStore.Calls.Add(feature);
                return ValueTask.FromResult(feature != "new-flavor");
            }).OnFeatureMissing((feature, _) => store.Missing.Add(feature)))
            .BuildServiceProvider();
        using var active = global::Engarde.Engarde.BeginScope(provider);
        var menu = new PizzaMenu();

        menu.PizzaType = PizzaTypeThatPeriodicallyExtends.Mushroom;
        Assert.Equal(PizzaTypeThatPeriodicallyExtends.Mushroom, menu.PizzaType);
        Assert.Equal(["custom-types"], store.Calls);

        store.Calls.Clear();
        menu.PizzaType = PizzaTypeThatPeriodicallyExtends.Pineapple;
        Assert.Equal(PizzaTypeThatPeriodicallyExtends.Mushroom, menu.PizzaType);
        Assert.Equal(["new-flavor", "custom-types"], store.Calls);
        Assert.Equal(["new-flavor"], store.Missing);

        store.Calls.Clear();
        menu.PizzaType = null;
        Assert.Null(menu.PizzaType);
        Assert.Empty(store.Calls);
    }

    [Fact]
    public void Generated_guard_has_a_value_selective_constructor()
    {
        var store = new ValueStore();
        using var provider = new ServiceCollection().AddSingleton(store)
            .AddEngarde(builder => builder.UseFeatureStore<ValueStore>((_, _) => ValueTask.FromResult(false))
                .OnFeatureMissing((feature, _) => store.Missing.Add(feature)))
            .BuildServiceProvider();
        using var active = global::Engarde.Engarde.BeginScope(provider);
        var menu = new GeneratedPizzaMenu();

        menu.PizzaType = PizzaTypeThatPeriodicallyExtends.Mushroom;
        Assert.Equal(PizzaTypeThatPeriodicallyExtends.Mushroom, menu.PizzaType);
        menu.PizzaType = PizzaTypeThatPeriodicallyExtends.Pineapple;
        Assert.Equal(PizzaTypeThatPeriodicallyExtends.Mushroom, menu.PizzaType);
        Assert.Equal(["new-flavor"], store.Missing);
    }

    [Fact]
    public void Selective_parameter_guard_checks_only_its_configured_argument()
    {
        var store = new ValueStore();
        using var provider = new ServiceCollection().AddSingleton(store)
            .AddEngarde(builder => builder.UseFeatureStore<ValueStore>((_, _) => ValueTask.FromResult(false))
                .OnFeatureMissing((feature, _) => throw new ParameterPizzaRejectedException(feature)))
            .BuildServiceProvider();
        using var active = global::Engarde.Engarde.BeginScope(provider);
        var service = new PizzaParameterService();

        service.Select(PizzaTypeThatPeriodicallyExtends.Mushroom);
        Assert.Equal(1, service.Calls);
        var error = Assert.Throws<ParameterPizzaRejectedException>(() =>
            service.Select(PizzaTypeThatPeriodicallyExtends.Pineapple));
        Assert.Equal("new-flavor", error.FeatureName);
        Assert.Equal(1, service.Calls);
    }

    [Fact]
    public void Generated_selective_guard_supports_parameters()
    {
        var store = new ValueStore();
        using var provider = new ServiceCollection().AddSingleton(store)
            .AddEngarde(builder => builder.UseFeatureStore<ValueStore>((_, _) => ValueTask.FromResult(false))
                .OnFeatureMissing((feature, _) => throw new ParameterPizzaRejectedException(feature)))
            .BuildServiceProvider();
        using var active = global::Engarde.Engarde.BeginScope(provider);
        var service = new PizzaParameterService();

        service.SelectGenerated(PizzaTypeThatPeriodicallyExtends.Mushroom);
        Assert.Equal(1, service.GeneratedCalls);
        Assert.Throws<ParameterPizzaRejectedException>(() =>
            service.SelectGenerated(PizzaTypeThatPeriodicallyExtends.Pineapple));
        Assert.Equal(1, service.GeneratedCalls);
    }

    private sealed class ValueStore
    {
        public List<string> Calls { get; } = [];
        public List<string> Missing { get; } = [];
    }
}

public sealed class ParameterPizzaRejectedException(string featureName) : Exception(featureName)
{
    public string FeatureName { get; } = featureName;
}

public enum PizzaTypeThatPeriodicallyExtends
{
    Pineapple = 1,
    Mushroom = 2
}

public sealed class PizzaMenu
{
    [Engarde(PizzaTypeThatPeriodicallyExtends.Pineapple, "new-flavor")]
    [Engarde("custom-types")]
    public PizzaTypeThatPeriodicallyExtends? PizzaType { get; set; }
}

public sealed partial class PizzaGuardAttribute(params string[] featureNames) : EngardeAttribute(featureNames);

public sealed class GeneratedPizzaMenu
{
    [PizzaGuard(PizzaTypeThatPeriodicallyExtends.Pineapple, "new-flavor")]
    public PizzaTypeThatPeriodicallyExtends? PizzaType { get; set; }
}

public sealed class PizzaParameterService
{
    public int Calls { get; private set; }
    public int GeneratedCalls { get; private set; }

    public void Select(
        [Engarde(PizzaTypeThatPeriodicallyExtends.Pineapple, "new-flavor")]
        PizzaTypeThatPeriodicallyExtends pizzaType) => Calls++;

    public void SelectGenerated(
        [PizzaGuard(PizzaTypeThatPeriodicallyExtends.Pineapple, "new-flavor")]
        PizzaTypeThatPeriodicallyExtends pizzaType) => GeneratedCalls++;
}
