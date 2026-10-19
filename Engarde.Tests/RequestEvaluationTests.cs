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
    public async Task Middleware_reuses_request_services_for_inline_and_woven_checks()
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
            Assert.True(await engarde.IsFeatureEnabledAsync("enabled"));
            await Task.Yield();
            RequestMembers.Enabled();
            Assert.True(await engarde.IsFeatureEnabledAsync("enabled"));
            RequestMembers.Disabled();
            RequestMembers.Disabled();
            Assert.Same(user, store.User);
            Assert.All(missing, item => Assert.Same(context.RequestServices, item.Services));
        });

        var request = services.CreateAsyncScope();
        var user = request.ServiceProvider.GetRequiredService<RequestUser>();
        await app.Build()(new DefaultHttpContext { RequestServices = request.ServiceProvider });

        Assert.Equal(5, calls.Count); // One store call for each check.
        Assert.Equal(2, missing.Count);
        Assert.All(calls, item => { Assert.Same(user, item.Store.User); Assert.Equal(7, item.Id); });
        Assert.False(user.Disposed);
        await request.DisposeAsync();
        Assert.True(user.Disposed);
    }

    [Fact]
    public async Task Concurrent_requests_use_their_own_services()
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
            var enabled = await engarde.IsFeatureEnabledAsync("enabled");
            if (Interlocked.Increment(ref arrivals) == 2) bothArrived.TrySetResult();
            await bothArrived.Task;
            Assert.Equal(user.Id == 7, enabled);
            Assert.Equal(enabled, await engarde.IsFeatureEnabledAsync("enabled"));
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
        Assert.Equal(6, calls.Count);
        Assert.Equal(3, calls.Select(item => item.Store).Distinct().Count());
        Assert.All(calls, item => Assert.True(item.Store.User.Disposed));
    }

    [Fact]
    public async Task Pipeline_exceptions_do_not_dispose_request_services()
    {
        await using var services = Services(new(), []);
        var app = new ApplicationBuilder(services);
        app.UseEngarde();
        app.Run(async context =>
        {
            var engarde = context.RequestServices.GetRequiredService<IEngarde>();
            Assert.True(await engarde.IsFeatureEnabledAsync("enabled"));
            throw new InvalidOperationException("endpoint failed");
        });

        await using var request = services.CreateAsyncScope();
        var user = request.ServiceProvider.GetRequiredService<RequestUser>();
        user.Id = 7;
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.Build()(new DefaultHttpContext { RequestServices = request.ServiceProvider }));
        Assert.False(user.Disposed);
    }

    [Fact]
    public async Task Borrowed_services_support_nested_scopes_and_restore_the_outer_services()
    {
        var calls = new ConcurrentQueue<(RequestStore Store, int Id)>();
        await using var services = Services(calls, []);
        await using var first = services.CreateAsyncScope();
        await using var second = services.CreateAsyncScope();
        first.ServiceProvider.GetRequiredService<RequestUser>().Id = 7;
        second.ServiceProvider.GetRequiredService<RequestUser>().Id = 8;
        using (Guard.BeginScope(first.ServiceProvider, createScope: false))
        {
            Assert.True(await Guard.CreateInstance().IsFeatureEnabledAsync("enabled"));
            using (Guard.BeginScope(second.ServiceProvider, createScope: false))
                Assert.False(await Guard.CreateInstance().IsFeatureEnabledAsync("enabled"));
            Assert.True(await Guard.CreateInstance().IsFeatureEnabledAsync("enabled"));
        }
        Assert.Equal(new[] { 7, 8, 7 }, calls.Select(call => call.Id));
        Assert.False(first.ServiceProvider.GetRequiredService<RequestUser>().Disposed);
        Assert.False(second.ServiceProvider.GetRequiredService<RequestUser>().Disposed);
    }

    [Fact]
    public async Task BeginScope_borrows_the_existing_scope()
    {
        var calls = new ConcurrentQueue<(RequestStore Store, int Id)>();
        await using var services = Services(calls, []);
        await using var request = services.CreateAsyncScope();
        var user = request.ServiceProvider.GetRequiredService<RequestUser>();
        user.Id = 8;

        using var active = Guard.BeginScope(request.ServiceProvider);
        Assert.False(await Guard.CreateInstance().IsFeatureEnabledAsync("enabled"));

        var call = Assert.Single(calls);
        Assert.Equal(8, call.Id);
        Assert.Same(user, call.Store.User);
        Assert.False(user.Disposed);
    }

    [Fact]
    public async Task Switching_BeginScope_uses_the_new_provider_until_disposed()
    {
        var calls = new ConcurrentQueue<(RequestStore Store, int Id)>();
        await using var services = Services(calls, []);
        await using var first = services.CreateAsyncScope();
        await using var second = services.CreateAsyncScope();
        first.ServiceProvider.GetRequiredService<RequestUser>().Id = 7;
        second.ServiceProvider.GetRequiredService<RequestUser>().Id = 8;

        using (Guard.BeginScope(first.ServiceProvider))
        {
            Assert.True(await Guard.CreateInstance().IsFeatureEnabledAsync("enabled"));
            using (Guard.BeginScope(second.ServiceProvider))
                Assert.False(await Guard.CreateInstance().IsFeatureEnabledAsync("enabled"));
            Assert.True(await Guard.CreateInstance().IsFeatureEnabledAsync("enabled"));
        }

        Assert.Equal(new[] { 7, 8, 7 }, calls.Select(call => call.Id));
    }

    private static ServiceProvider Services(ConcurrentQueue<(RequestStore Store, int Id)> calls, List<EngardeContext> missing) =>
        new ServiceCollection().AddScoped(_ => new RequestUser { Id = 7 }).AddScoped<RequestStore>()
            .AddEngarde(builder => builder.UseFeatureStore<RequestStore, RequestUser, int>(
                (store, flag, id) => { calls.Enqueue((store, id)); return ValueTask.FromResult(flag == "enabled" && id == 7); },
                user => ValueTask.FromResult(user.Id))
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
