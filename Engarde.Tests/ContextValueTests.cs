using Engarde;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Tests;

public sealed class ContextValueTests
{
    private static ServiceProvider Services(List<EngardeContext> contexts) => new ServiceCollection()
        .AddSingleton(new Store())
        .AddEngarde(builder => builder.UseFeatureStore<Store>((_, _) => ValueTask.FromResult(false))
            .OnFeatureMissing((_, context) => contexts.Add(context)))
        .BuildServiceProvider();

    [Fact]
    public async Task Methods_and_lambdas_receive_null_values_even_when_results_are_cached()
    {
        var contexts = new List<EngardeContext>();
        await using var services = Services(contexts);
        using var active = Engarde.Engarde.BeginScope(services);
        Subject.Run();
        Subject.Run();
        var calls = 0;
        Action handler = [Engarde("disabled")] () => calls++;
        handler();
        Assert.Equal(1, calls);
        Assert.Equal(3, contexts.Count);
        Assert.All(contexts, context => Assert.Null(context.Value));
        Assert.Equal(nameof(Subject.Run), contexts[0].Member.Name);
        Assert.Equal(handler.Method, contexts[2].Member);
    }

    [Fact]
    public async Task Assignments_and_parameters_keep_incoming_values_and_constructors_receive_null()
    {
        var contexts = new List<EngardeContext>();
        await using var services = Services(contexts);
        using var active = Engarde.Engarde.BeginScope(services);
        var subject = new AssignmentSubject();
        subject.Name = "property value";
        subject.Field = "field value";
        subject.Accept("parameter value");
        _ = new ConstructorSubject();
        Assert.Equal(4, contexts.Count);
        Assert.Equal("property value", contexts[0].Value);
        Assert.Equal("field value", contexts[1].Value);
        Assert.Equal("parameter value", contexts[2].Value);
        Assert.Null(contexts[3].Value);
    }

    [Fact]
    public async Task Disabled_field_and_property_writes_are_ignored_after_handler_receives_attempted_value()
    {
        var contexts = new List<EngardeContext>();
        await using var services = Services(contexts);
        using var active = Engarde.Engarde.BeginScope(services);
        var subject = new RetainedAssignmentSubject();
        typeof(RetainedAssignmentSubject).GetField(nameof(RetainedAssignmentSubject.Field))!
            .SetValue(subject, "original field");
        typeof(RetainedAssignmentSubject).GetField(nameof(RetainedAssignmentSubject.StaticField))!
            .SetValue(null, "original static field");

        subject.Name = "attempted property";
        subject.Field = "attempted field";
        RetainedAssignmentSubject.StaticField = "attempted static field";

        Assert.Equal("original property", subject.Name);
        Assert.Equal("original field", subject.Field);
        Assert.Equal("original static field", RetainedAssignmentSubject.StaticField);
        Assert.Equal(0, subject.SetterCalls);
        Assert.Collection(contexts,
            context => Assert.Equal("attempted property", context.Value),
            context => Assert.Equal("attempted field", context.Value),
            context => Assert.Equal("attempted static field", context.Value));
    }

    [Fact]
    public async Task Inline_query_value_remains_null()
    {
        var contexts = new List<EngardeContext>();
        await using var services = Services(contexts);
        using var active = Engarde.Engarde.BeginScope(services);
        Assert.False(await Engarde.Engarde.CreateInstance().IsFeatureEnabledAsync(new InspectingGuard(contexts.Add)));
        // The guard's evaluation context, then the missing-feature handler's context
        Assert.Equal(2, contexts.Count);
        Assert.All(contexts, context =>
        {
            Assert.Null(context.Value);
            Assert.Equal(typeof(InspectingGuard), context.Member);
        });
    }

    private sealed class InspectingGuard(Action<EngardeContext> observe) : EngardeAttribute("disabled")
    {
        protected override ValueTask<bool> IsEnabledAsync(string featureName, EngardeContext context, CancellationToken cancellationToken)
        {
            observe(context);
            return ValueTask.FromResult(false);
        }
    }

    private static class Subject
    {
        [Engarde("disabled")] public static void Run() { }
    }

    private sealed class AssignmentSubject
    {
        [Engarde("disabled")] public string? Name { get; set; }
        [Engarde("disabled")] public string? Field;
        public void Accept([Engarde("disabled")] string name) { }
    }

    private sealed class RetainedAssignmentSubject
    {
        private string name = "original property";
        [Engarde("disabled")]
        public string Name
        {
            get => name;
            set { SetterCalls++; name = value; }
        }

        [Engarde("disabled")] public string? Field;
        [Engarde("disabled")] public static string? StaticField;
        public int SetterCalls { get; private set; }
    }

    private sealed class ConstructorSubject
    {
        [Engarde("disabled")] public ConstructorSubject() { }
    }
}
