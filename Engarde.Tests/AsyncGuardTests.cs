using Engarde;
using Xunit;

namespace Tests;

public sealed class AsyncGuardTests
{
    [Fact]
    public async Task Task_guard_returns_before_asynchronous_check_and_starts_body_after_it_passes()
    {
        DelayedGuardAttribute.Reset();

        var invocation = AsyncGuarded.TaskAsync(42);

        Assert.False(invocation.IsCompleted);
        Assert.False(AsyncGuarded.BodyStarted);
        DelayedGuardAttribute.Complete(true);
        Assert.Equal(42, await invocation);
        Assert.True(AsyncGuarded.BodyStarted);
    }

    [Fact]
    public async Task ValueTask_and_generic_method_guards_use_the_awaitable_path()
    {
        DelayedGuardAttribute.Reset();
        var invocation = AsyncGuarded.ValueTaskAsync("result");
        Assert.False(invocation.IsCompleted);
        Assert.False(AsyncGuarded.BodyStarted);
        DelayedGuardAttribute.Complete(true);
        Assert.Equal("result", await invocation);

        DelayedGuardAttribute.Reset();
        var generic = AsyncGuarded.GenericAsync(17);
        Assert.False(generic.IsCompleted);
        DelayedGuardAttribute.Complete(true);
        Assert.Equal(17, await generic);
    }

    [Fact]
    public async Task Disabled_async_guard_faults_task_without_starting_method_body()
    {
        DelayedGuardAttribute.Reset();
        var invocation = AsyncGuarded.TaskAsync(42);
        DelayedGuardAttribute.Complete(false);
        await Assert.ThrowsAsync<DisabledFeatureException>(() => invocation);
        Assert.False(AsyncGuarded.BodyStarted);
    }

    [Fact]
    public async Task Default_value_on_async_guarded_parameter_is_skipped()
    {
        DelayedGuardAttribute.Reset();
        Assert.Equal(0, await AsyncGuarded.ParameterAsync(0));
    }

    [Fact]
    public async Task Non_generic_Task_and_ValueTask_instance_methods_use_the_awaitable_path()
    {
        var subject = new AsyncInstanceGuarded();
        DelayedGuardAttribute.Reset();
        var task = subject.TaskAsync();
        Assert.False(task.IsCompleted);
        Assert.False(subject.BodyStarted);
        DelayedGuardAttribute.Complete(true);
        await task;
        Assert.True(subject.BodyStarted);

        subject.BodyStarted = false;
        DelayedGuardAttribute.Reset();
        var valueTask = subject.ValueTaskAsync();
        Assert.False(valueTask.IsCompleted);
        Assert.False(subject.BodyStarted);
        DelayedGuardAttribute.Complete(true);
        await valueTask;
        Assert.True(subject.BodyStarted);
    }
}

public sealed class DelayedGuardAttribute() : EngardeAttribute("delayed")
{
    private static TaskCompletionSource<bool> completion = NewCompletion();

    internal static void Reset()
    {
        completion = NewCompletion();
        AsyncGuarded.BodyStarted = false;
    }

    internal static void Complete(bool enabled) => completion.SetResult(enabled);

    protected override ValueTask<bool> IsEnabledAsync(string featureName, EngardeContext context) =>
        new(completion.Task);

    protected override void OnFeatureMissing(string featureName, EngardeContext context) =>
        throw new DisabledFeatureException(featureName);

    private static TaskCompletionSource<bool> NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public static class AsyncGuarded
{
    internal static bool BodyStarted;

    [DelayedGuard]
    public static async Task<int> TaskAsync(int value)
    {
        BodyStarted = true;
        await Task.Yield();
        return value;
    }

    [DelayedGuard]
    public static async ValueTask<string> ValueTaskAsync(string value)
    {
        BodyStarted = true;
        await Task.Yield();
        return value;
    }

    [DelayedGuard]
    public static async Task<T> GenericAsync<T>(T value)
    {
        BodyStarted = true;
        await Task.Yield();
        return value;
    }

    public static async Task<int> ParameterAsync([DelayedGuard] int value)
    {
        await Task.Yield();
        return value;
    }
}

public sealed class AsyncInstanceGuarded
{
    internal bool BodyStarted;

    [DelayedGuard]
    public async Task TaskAsync()
    {
        BodyStarted = true;
        await Task.Yield();
    }

    [DelayedGuard]
    public async ValueTask ValueTaskAsync()
    {
        BodyStarted = true;
        await Task.Yield();
    }
}
