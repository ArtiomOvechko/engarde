using System.Reflection;
using Engarde;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Guard = Engarde.Engarde;

namespace Tests;

public sealed class InheritedGuardTests
{
    private static ServiceProvider Services(Recorder recorder) => new ServiceCollection()
        .AddSingleton(recorder)
        .AddEngarde(builder => builder
            .UseFeatureStore<Recorder>((store, featureName) =>
            {
                store.Checked.Add(featureName);
                return ValueTask.FromResult(store.Enabled.Contains(featureName));
            })
            .OnFeatureMissing((featureName, context) => recorder.Missing.Add((featureName, context.Member))))
        .BuildServiceProvider();

    private sealed class Session : IDisposable
    {
        private readonly ServiceProvider _services;
        private readonly IDisposable _scope;

        public Session()
        {
            _services = Services(Recorder);
            _scope = Guard.BeginScope(_services);
        }

        public Recorder Recorder { get; } = new();

        public void Dispose()
        {
            _scope.Dispose();
            _services.Dispose();
        }
    }

    [Fact]
    public void Interface_guards_are_checked_once_when_any_implementor_is_constructed()
    {
        using var session = new Session();
        var recorder = session.Recorder;

        _ = new DirectImplementation();
        _ = new IndirectImplementation();
        _ = new DerivedImplementation();
        _ = new GuardedStruct(1);

        Assert.Equal(new[] { "iface", "iface", "iface", "iface" }, recorder.Checked);
        Assert.Equal(typeof(DirectImplementation), recorder.Missing[0].Member.DeclaringType);
        // A derived class is checked by the base constructor that introduces the interface
        Assert.Equal(typeof(BaseImplementation), recorder.Missing[2].Member.DeclaringType);
    }

    [Fact]
    public void Interface_guards_apply_to_implementors_methods_and_default_implementations()
    {
        using var session = new Session();
        var recorder = session.Recorder;
        var implementation = new DirectImplementation();
        var derived = new DerivedImplementation();
        recorder.Checked.Clear();

        implementation.Run();
        ((IGuardedService)implementation).Default();
        derived.Run();

        Assert.Equal(new[] { "iface", "iface", "iface" }, recorder.Checked);
        Assert.Equal(typeof(IGuardedService), recorder.Missing[^2].Member.DeclaringType);
        Assert.Equal(typeof(BaseImplementation), recorder.Missing[^1].Member.DeclaringType);
    }

    [Fact]
    public void Inherited_guards_run_alongside_closer_guards_naming_the_same_feature()
    {
        using var session = new Session();
        var recorder = session.Recorder;
        RecordingGuard.Checked.Value = [];

        _ = new OverridingImplementation();
        new CloserOverride().Run();

        Assert.Equal(new[] { "iface", "virtual", "again" }, RecordingGuard.Checked.Value);
        Assert.Equal(new[] { "iface", "other", "again", "virtual" }, recorder.Checked);
    }

    [Fact]
    public void Member_and_type_guards_naming_the_same_feature_both_check_it()
    {
        using var session = new Session();
        var recorder = session.Recorder;
        var shared = new SharedFeature();
        recorder.Checked.Clear();
        RecordingGuard.Checked.Value = [];

        shared.Run();

        Assert.Equal(new[] { "shared" }, RecordingGuard.Checked.Value);
        Assert.Equal(new[] { "shared" }, recorder.Checked);
    }

    [Fact]
    public async Task Interface_member_guards_apply_to_inherited_implementations_on_top_of_the_base_guards()
    {
        using var session = new Session();
        var recorder = session.Recorder;
        IMemberGuarded implementation = new ReimplementingMembers();
        Assert.Equal(new[] { "base-type" }, recorder.Checked);
        recorder.Checked.Clear();

        implementation.Run();
        implementation.Name = "skipped";
        implementation.Update(1);
        await implementation.RunAsync();

        Assert.Equal(new[] { "method", "base-member", "base-type", "property", "parameter", "base-type", "async", "base-type" },
            recorder.Checked);
        Assert.Null(implementation.Name);
    }

    [Fact]
    public void Interface_member_guards_apply_to_inherited_generic_implementations()
    {
        using var session = new Session();
        var recorder = session.Recorder;
        IGenericGuarded<int> implementation = new GenericReimplementation();

        implementation.Handle(1);
        implementation.Map("value");

        Assert.Equal(new[] { "generic", "generic-method" }, recorder.Checked);
    }

    [Fact]
    public void Base_class_guards_apply_to_derived_methods_and_run_once_on_construction()
    {
        using var session = new Session();
        var recorder = session.Recorder;

        var derived = new DerivedFromGuarded();
        Assert.Equal(new[] { "base" }, recorder.Checked);
        derived.DerivedMethod();
        Assert.Equal(new[] { "base", "base" }, recorder.Checked);
        Assert.Equal(typeof(DerivedFromGuarded), recorder.Missing[1].Member.DeclaringType);
    }

    [Theory]
    [InlineData(typeof(MemberImplementation))]
    [InlineData(typeof(ExplicitMemberImplementation))]
    public async Task Interface_member_guards_apply_to_implicit_and_explicit_implementations(Type type)
    {
        using var session = new Session();
        var recorder = session.Recorder;
        var implementation = (IMemberGuarded)Activator.CreateInstance(type)!;

        implementation.Run();
        implementation.Name = "skipped";
        implementation.Name = null;
        implementation.Update(0);
        implementation.Update(1);
        await implementation.RunAsync();

        Assert.Equal(new[] { "method", "property", "parameter", "async" }, recorder.Checked);
        Assert.Null(implementation.Name);
    }

    [Fact]
    public void Overriding_members_inherit_guards_from_every_overridden_declaration()
    {
        using var session = new Session();
        var recorder = session.Recorder;
        var members = new OverridingAgain();

        members.Run();
        members.Name = "skipped";

        Assert.Equal(new[] { "again", "virtual", "virtual-property" }, recorder.Checked);
        Assert.Null(members.Name);
    }

    [Fact]
    public void Generic_interface_member_guards_apply_to_closed_open_and_generic_method_implementations()
    {
        using var session = new Session();
        var recorder = session.Recorder;

        new GenericImplementation().Handle(1);
        new OpenGenericImplementation<string>().Handle("value");
        new GenericImplementation().Map("value");

        Assert.Equal(new[] { "generic", "generic", "generic-method" }, recorder.Checked);
    }
}

public sealed class Recorder
{
    public HashSet<string> Enabled { get; } = [];
    public List<string> Checked { get; } = [];
    public List<(string FeatureName, MemberInfo Member)> Missing { get; } = [];
}

public sealed class RecordingGuard(params string[] featureNames) : EngardeAttribute(featureNames)
{
    public static readonly AsyncLocal<List<string>> Checked = new();

    protected override ValueTask<bool> IsEnabledAsync(string featureName, EngardeContext context)
    {
        Checked.Value?.Add(featureName);
        return ValueTask.FromResult(true);
    }
}

[Engarde("iface")]
public interface IGuardedService
{
    void Run();
    void Default() { }
}

public interface IDerivedService : IGuardedService;

public sealed class DirectImplementation : IGuardedService
{
    public void Run() { }
}

public sealed class IndirectImplementation : IDerivedService
{
    public void Run() { }
}

public class BaseImplementation : IGuardedService
{
    public virtual void Run() { }
}

public sealed class DerivedImplementation : BaseImplementation;

public readonly struct GuardedStruct : IGuardedService
{
    public GuardedStruct(int value) => Value = value;
    public int Value { get; }
    public void Run() { }
}

[Engarde("iface", "other")]
public interface IOverriddenService;

[RecordingGuard("iface")]
public sealed class OverridingImplementation : IOverriddenService;

[Engarde("base")]
public class GuardedBase;

public sealed class DerivedFromGuarded : GuardedBase
{
    public void DerivedMethod() { }
}

public interface IMemberGuarded
{
    [Engarde("method")] void Run();
    [Engarde("property")] string? Name { get; set; }
    void Update([Engarde("parameter")] int version);
    [Engarde("async")] Task RunAsync();
}

public sealed class MemberImplementation : IMemberGuarded
{
    public void Run() { }
    public string? Name { get; set; }
    public void Update(int version) { }
    public Task RunAsync() => Task.CompletedTask;
}

public sealed class ExplicitMemberImplementation : IMemberGuarded
{
    void IMemberGuarded.Run() { }
    string? IMemberGuarded.Name { get; set; }
    void IMemberGuarded.Update(int version) { }
    Task IMemberGuarded.RunAsync() => Task.CompletedTask;
}

public abstract class GuardedMembersBase
{
    [Engarde("virtual")] public abstract void Run();
    [Engarde("virtual-property")] public virtual string? Name { get; set; }
}

public class OverridingMembers : GuardedMembersBase
{
    [Engarde("again")] public override void Run() { }
    public override string? Name { get; set; }
}

public sealed class OverridingAgain : OverridingMembers
{
    public override void Run() { }
}

public sealed class CloserOverride : OverridingMembers
{
    [RecordingGuard("virtual", "again")] public override void Run() { }
}

public interface IGenericGuarded<T>
{
    [Engarde("generic")] void Handle(T value);
    [Engarde("generic-method")] void Map<TValue>(TValue value);
}

public sealed class GenericImplementation : IGenericGuarded<int>
{
    public void Handle(int value) { }
    public void Map<TOther>(TOther value) { }
}

public sealed class OpenGenericImplementation<T> : IGenericGuarded<T>
{
    public void Handle(T value) { }
    public void Map<TValue>(TValue value) { }
}

[Engarde("shared")]
public sealed class SharedFeature
{
    [RecordingGuard("shared")] public void Run() { }
}

[Engarde("base-type")]
public class PlainMembersBase
{
    [Engarde("base-member")] public virtual void Run() { }
    public string? Name { get; set; }
    public void Update(int version) { }
    public Task RunAsync() => Task.CompletedTask;
}

public sealed class ReimplementingMembers : PlainMembersBase, IMemberGuarded;

public class GenericBase
{
    public void Handle(int value) { }
    public void Map<TOther>(TOther value) { }
}

public sealed class GenericReimplementation : GenericBase, IGenericGuarded<int>;
