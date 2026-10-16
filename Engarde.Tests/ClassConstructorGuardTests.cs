using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Engarde;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Guard = Engarde.Engarde;

namespace Tests;

public sealed class ClassConstructorGuardTests
{
    [Fact]
    public void Implicit_default_constructor_is_checked_before_instance_initializers_on_every_call()
    {
        var gate = new Gate();
        using var services = Services(gate);
        using var active = Guard.BeginScope(services);
        Assert.Throws<DisabledFeatureException>(() => new ImplicitDefault());
        Assert.Equal(0, Effects.Count(typeof(ImplicitDefault)));
        gate.Enabled = true;
        _ = new ImplicitDefault();
        _ = new ImplicitDefault();
        Assert.Equal(2, Effects.Count(typeof(ImplicitDefault)));
        Assert.Equal(3, gate.Calls);
        AssertConstructor(Assert.Single(gate.Missing), typeof(ImplicitDefault), isStatic: false);
    }

    [Fact]
    public void Every_explicit_constructor_overload_is_guarded()
    {
        var gate = new Gate();
        using var services = Services(gate);
        using var active = Guard.BeginScope(services);
        Action[] constructors = [() => new Overloaded(), () => new Overloaded(7), () => new Overloaded("text")];
        foreach (var construct in constructors)
            Assert.Throws<DisabledFeatureException>(construct);
        Assert.Equal(0, Effects.Count(typeof(Overloaded)));
        gate.Enabled = true;
        foreach (var construct in constructors) construct();
        Assert.Equal(3, Effects.Count(typeof(Overloaded)));
        Assert.Equal(6, gate.Calls);
        Assert.Equal(3, gate.Missing.Count);
        Assert.All(gate.Missing, context => AssertConstructor(context, typeof(Overloaded), isStatic: false));
    }

    [Fact]
    public void Constructor_chaining_checks_each_constructor_even_when_the_result_is_cached()
    {
        var gate = new Gate { ThrowOnMissing = false };
        using var services = Services(gate);
        using var active = Guard.BeginScope(services);
        using var evaluation = Guard.BeginEvaluationScope();
        _ = new Chained();
        Assert.Equal(1, gate.Calls);
        Assert.Equal(2, gate.Missing.Count);
        Assert.All(gate.Missing, context => AssertConstructor(context, typeof(Chained), isStatic: false));
    }

    [Fact]
    public void Explicit_static_constructor_executes_the_class_guard_once_before_its_body()
    {
        var gate = new Gate { Enabled = true };
        using var services = Services(gate);
        using var active = Guard.BeginScope(services);
        RuntimeHelpers.RunClassConstructor(typeof(AllowedStatic).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(AllowedStatic).TypeHandle);
        Assert.Equal(1, gate.Calls);
        Assert.Equal(1, Effects.Count(typeof(AllowedStatic)));
    }

    [Fact]
    public void Disabled_static_constructor_is_blocked_and_keeps_CLR_initialization_failure_semantics()
    {
        var gate = new Gate();
        using var services = Services(gate);
        using var active = Guard.BeginScope(services);
        var error = Assert.Throws<TypeInitializationException>(() => RuntimeHelpers.RunClassConstructor(typeof(BlockedStatic).TypeHandle));
        Assert.IsType<DisabledFeatureException>(error.InnerException);
        Assert.Equal(0, Effects.Count(typeof(BlockedStatic)));
        AssertConstructor(Assert.Single(gate.Missing), typeof(BlockedStatic), isStatic: true);
        gate.Enabled = true;
        Assert.Throws<TypeInitializationException>(() => RuntimeHelpers.RunClassConstructor(typeof(BlockedStatic).TypeHandle));
        Assert.Equal(1, gate.Calls);
    }

    [Fact]
    public void Compiler_emitted_static_initializer_is_guarded_and_cannot_run_early()
    {
        var gate = new Gate();
        using var services = Services(gate);
        using var active = Guard.BeginScope(services);
        Assert.False(typeof(ImplicitStatic).Attributes.HasFlag(TypeAttributes.BeforeFieldInit));
        var error = Assert.Throws<TypeInitializationException>(() => _ = ImplicitStatic.Value);
        Assert.IsType<DisabledFeatureException>(error.InnerException);
        Assert.Equal(0, Effects.Count(typeof(ImplicitStatic)));
        AssertConstructor(Assert.Single(gate.Missing), typeof(ImplicitStatic), isStatic: true);
    }

    [Fact]
    public void Instance_creation_runs_both_static_and_implicit_default_constructor_guards()
    {
        var gate = new Gate { ThrowOnMissing = false };
        using var services = Services(gate);
        using var active = Guard.BeginScope(services);
        using var evaluation = Guard.BeginEvaluationScope();
        _ = new BothConstructors();
        _ = new BothConstructors();
        Assert.Equal(1, gate.Calls);
        Assert.Equal(new[] { ".cctor", ".ctor", ".ctor" }, gate.Missing.Select(context => context.Member.Name));
        Assert.Equal(1, Effects.Count(typeof(BothConstructors)));
    }

    [Fact]
    public void Generic_static_constructors_are_checked_once_per_closed_type()
    {
        var gate = new Gate { Enabled = true };
        using var services = Services(gate);
        using var active = Guard.BeginScope(services);
        RuntimeHelpers.RunClassConstructor(typeof(GenericStatic<int>).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(GenericStatic<string>).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(GenericStatic<int>).TypeHandle);
        Assert.Equal(2, gate.Calls);
        Assert.Equal(1, Effects.Count(typeof(GenericStatic<int>)));
        Assert.Equal(1, Effects.Count(typeof(GenericStatic<string>)));
    }

    private static ServiceProvider Services(Gate gate) => new ServiceCollection().AddSingleton(gate)
        .AddEngarde(builder => builder.UseFeatureStore<Gate>((store, _) =>
        {
            Interlocked.Increment(ref store.Calls);
            return ValueTask.FromResult(store.Enabled);
        }).OnFeatureMissing((flag, context) =>
        {
            gate.Missing.Add(context);
            if (gate.ThrowOnMissing) throw new DisabledFeatureException(flag);
        })).BuildServiceProvider();

    private static void AssertConstructor(EngardeContext context, Type type, bool isStatic)
    {
        var constructor = Assert.IsAssignableFrom<ConstructorInfo>(context.Member);
        Assert.Equal(type, constructor.DeclaringType);
        Assert.Equal(isStatic, constructor.IsStatic);
        Assert.Null(context.Value);
    }

    private sealed class Gate
    {
        public bool Enabled;
        public bool ThrowOnMissing = true;
        public int Calls;
        public List<EngardeContext> Missing { get; } = [];
    }

    private static class Effects
    {
        private static readonly ConcurrentDictionary<Type, int> Counts = new();
        public static int Record(Type type) => Counts.AddOrUpdate(type, 1, static (_, count) => count + 1);
        public static int Count(Type type) => Counts.GetValueOrDefault(type);
    }

    [Engarde("constructors")]
    private sealed class ImplicitDefault
    {
        public readonly int Value = Effects.Record(typeof(ImplicitDefault));
    }

    [Engarde("constructors")]
    private sealed class Overloaded
    {
        public Overloaded() => Effects.Record(typeof(Overloaded));
        public Overloaded(int _) => Effects.Record(typeof(Overloaded));
        internal Overloaded(string _) => Effects.Record(typeof(Overloaded));
    }

    [Engarde("constructors")]
    private sealed class Chained
    {
        public Chained() : this(7) { }
        private Chained(int _) { }
    }

    private sealed class ConstructorGuardAttribute() : EngardeAttribute("constructors");

    [ConstructorGuard]
    private static class AllowedStatic
    {
        static AllowedStatic() => Effects.Record(typeof(AllowedStatic));
    }

    [Engarde("constructors")]
    private static class BlockedStatic
    {
        static BlockedStatic() => Effects.Record(typeof(BlockedStatic));
    }

    [Engarde("constructors")]
    private sealed class ImplicitStatic
    {
        public static readonly int Value = Effects.Record(typeof(ImplicitStatic));
    }

    [Engarde("constructors")]
    private sealed class BothConstructors
    {
        static BothConstructors() => Effects.Record(typeof(BothConstructors));
    }

    [Engarde("constructors")]
    private sealed class GenericStatic<T>
    {
        static GenericStatic() => Effects.Record(typeof(GenericStatic<T>));
    }
}
