using System.Collections.Concurrent;
using Engarde;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Guard = Engarde.Engarde;

namespace Tests;

public sealed class RequestEvaluationTests
{
    [Fact]
    public async Task Middleware_reuses_request_services_and_shares_inline_and_woven_results()
    {
        var calls = new ConcurrentQueue<(RequestStore Store, int Id)>();
        var missing = new List<EngardeContext>();
        await using var services = Services(calls, missing);
        var app = new ApplicationBuilder(services);
        app.UseEngarde();
        app.Run(async context =>
        {
            var user = context.RequestServices.GetRequiredService<RequestUser>();
            var store = context.RequestServices.GetRequiredService<RequestStore>();
            var engarde = context.RequestServices.GetRequiredService<IEngarde>();
            user.Id = 7;
            Assert.True(await engarde.IsEnabledAsync("enabled"));
            await Task.Yield();
            RequestMembers.Enabled();
            Assert.True(await engarde.IsEnabledAsync("enabled"));
            RequestMembers.Disabled();
            RequestMembers.Disabled();
            Assert.Same(user, store.User);
            Assert.All(missing, item => Assert.Same(context.RequestServices, item.Services));
        });

        var request = services.CreateAsyncScope();
        var user = request.ServiceProvider.GetRequiredService<RequestUser>();
        await app.Build()(new DefaultHttpContext { RequestServices = request.ServiceProvider });

        Assert.Equal(2, calls.Count); // One store call for each flag.
        Assert.Equal(2, missing.Count); // The cached false still invokes each operation's handler.
        Assert.All(calls, item => { Assert.Same(user, item.Store.User); Assert.Equal(7, item.Id); });
        Assert.False(user.Disposed);
        await request.DisposeAsync();
        Assert.True(user.Disposed);
    }

    [Fact]
    public async Task Concurrent_requests_have_separate_results_and_the_next_request_refreshes_them()
    {
        var calls = new ConcurrentQueue<(RequestStore Store, int Id)>();
        await using var services = Services(calls, []);
        var arrivals = 0;
        var bothArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var app = new ApplicationBuilder(services);
        app.UseEngarde();
        app.Run(async context =>
        {
            var user = context.RequestServices.GetRequiredService<RequestUser>();
            var engarde = context.RequestServices.GetRequiredService<IEngarde>();
            user.Id = (int)context.Items["id"]!;
            var enabled = await engarde.IsEnabledAsync("enabled");
            if (Interlocked.Increment(ref arrivals) == 2) bothArrived.TrySetResult();
            await bothArrived.Task;
            Assert.Equal(user.Id == 7, enabled);
            Assert.Equal(enabled, await engarde.IsEnabledAsync("enabled"));
        });
        var pipeline = app.Build();
        async Task Request(int id)
        {
            await using var request = services.CreateAsyncScope();
            var context = new DefaultHttpContext { RequestServices = request.ServiceProvider };
            context.Items["id"] = id;
            await pipeline(context);
        }

        await Task.WhenAll(Request(7), Request(8)).WaitAsync(TimeSpan.FromSeconds(10));
        await Request(7);
        Assert.Equal(3, calls.Count);
        Assert.Equal(3, calls.Select(item => item.Store).Distinct().Count());
        Assert.All(calls, item => Assert.True(item.Store.User.Disposed));
    }

    [Fact]
    public async Task Pipeline_exceptions_end_the_cache_without_disposing_request_services()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? escaped = null;
        await using var services = Services(new(), []);
        var app = new ApplicationBuilder(services);
        app.UseEngarde();
        app.Run(async context =>
        {
            var engarde = context.RequestServices.GetRequiredService<IEngarde>();
            Assert.True(await engarde.IsEnabledAsync("enabled"));
            escaped = Task.Run(async () => { await release.Task; return await engarde.IsEnabledAsync("enabled"); });
            throw new InvalidOperationException("endpoint failed");
        });

        await using var request = services.CreateAsyncScope();
        var user = request.ServiceProvider.GetRequiredService<RequestUser>();
        user.Id = 7;
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.Build()(new DefaultHttpContext { RequestServices = request.ServiceProvider }));
        Assert.False(user.Disposed);
        release.SetResult();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => escaped!);
    }

    [Fact]
    public async Task Borrowed_services_support_nested_scopes_and_restore_the_outer_cache()
    {
        var calls = new ConcurrentQueue<(RequestStore Store, int Id)>();
        await using var services = Services(calls, []);
        await using var first = services.CreateAsyncScope();
        await using var second = services.CreateAsyncScope();
        first.ServiceProvider.GetRequiredService<RequestUser>().Id = 7;
        second.ServiceProvider.GetRequiredService<RequestUser>().Id = 8;
        using (Guard.BeginEvaluationScope(first.ServiceProvider))
        {
            Assert.True(await Guard.CreateInstance().IsEnabledAsync("enabled"));
            using (Guard.BeginEvaluationScope(second.ServiceProvider))
                Assert.False(await Guard.CreateInstance().IsEnabledAsync("enabled"));
            Assert.True(await Guard.CreateInstance().IsEnabledAsync("enabled"));
        }
        Assert.Equal(2, calls.Count);
        Assert.False(first.ServiceProvider.GetRequiredService<RequestUser>().Disposed);
        Assert.False(second.ServiceProvider.GetRequiredService<RequestUser>().Disposed);
    }

    [Fact]
    public async Task BeginScope_and_parameterless_evaluation_borrow_the_existing_scope()
    {
        var calls = new ConcurrentQueue<(RequestStore Store, int Id)>();
        await using var services = Services(calls, []);
        await using var request = services.CreateAsyncScope();
        var user = request.ServiceProvider.GetRequiredService<RequestUser>();
        user.Id = 8;

        using var active = Guard.BeginScope(request.ServiceProvider);
        using var evaluation = Guard.BeginEvaluationScope();
        Assert.False(await Guard.CreateInstance().IsEnabledAsync("enabled"));

        var call = Assert.Single(calls);
        Assert.Equal(8, call.Id);
        Assert.Same(user, call.Store.User);
        Assert.False(user.Disposed);
    }

    [Fact]
    public async Task Switching_BeginScope_does_not_reuse_the_previous_providers_evaluation()
    {
        var calls = new ConcurrentQueue<(RequestStore Store, int Id)>();
        await using var services = Services(calls, []);
        await using var first = services.CreateAsyncScope();
        await using var second = services.CreateAsyncScope();
        first.ServiceProvider.GetRequiredService<RequestUser>().Id = 7;
        second.ServiceProvider.GetRequiredService<RequestUser>().Id = 8;

        using (Guard.BeginScope(first.ServiceProvider))
        using (Guard.BeginEvaluationScope())
        {
            Assert.True(await Guard.CreateInstance().IsEnabledAsync("enabled"));
            using (Guard.BeginScope(second.ServiceProvider))
                Assert.False(await Guard.CreateInstance().IsEnabledAsync("enabled"));
            Assert.True(await Guard.CreateInstance().IsEnabledAsync("enabled"));
        }

        Assert.Equal(new[] { 7, 8 }, calls.Select(call => call.Id));
    }

    private static ServiceProvider Services(ConcurrentQueue<(RequestStore Store, int Id)> calls, List<EngardeContext> missing) =>
        new ServiceCollection().AddScoped(_ => new RequestUser { Id = 7 }).AddScoped<RequestStore>()
            .AddEngarde(builder => builder.UseFeatureStore<RequestStore, RequestUser, int>(
                user => ValueTask.FromResult(user.Id),
                (store, flag, id) => { calls.Enqueue((store, id)); return ValueTask.FromResult(flag == "enabled" && id == 7); })
                .OnFeatureMissing((_, context) => missing.Add(context)))
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

    private sealed class RequestUser : IAsyncDisposable
    {
        public int Id;
        public bool Disposed;
        public ValueTask DisposeAsync() { Disposed = true; return default; }
    }

    private sealed class RequestStore(RequestUser user) { public RequestUser User => user; }

    private static class RequestMembers
    {
        [Engarde("enabled")] public static void Enabled() { }
        [Engarde("disabled")] public static void Disabled() { }
    }
}
