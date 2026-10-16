# Engarde

Engarde compiles feature-toggle guards into methods, constructors, types, properties, fields, parameters, and injectable asynchronous queries.

## Quick start

Register an ASP.NET Core feature store with a scoped ID provider, add the request evaluation scope, then use guards as attributes or inject `IEngarde` for inline checks.

```sh
dotnet add package Engarde
```

```csharp
using System.Security.Claims;
using Engarde;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEngarde(engarde => engarde
    //Engarde needs a couple of things to run: 
    //1. a feature toggle service to use (the feature service is resolved with DI, so it has to be registered in the service colletion)
    //(optional) 2. a factory to resolve scope ID (a user resolver in this example) to check feature availability for
    .UseFeatureStore<AppFeatureStore, CurrentUserService, string>(
        user => ValueTask.FromResult(user.Id),
        (store, featureName, userId) =>
            store.IsEnabledAsync(featureName, userId))
        
    //Also, you can define global behavior what happens if the flag check is not passed
    .OnFeatureMissing((featureName, context) =>
        throw new InvalidOperationException(
            $"Feature '{featureName}' is required by {context.Member.Name}.")));
    
var app = builder.Build();

app.UseEngarde();

//Can be any service that resolves scope relevant to feature limiting
public sealed class CurrentUserService(...)
{
    public string Id => return SomeService.GetCurrentUserId();
}

//A service to perform feature check against your scope (user Id in this example)
public class AppFeatureStore
{
    ...
    public ValueTask<bool> IsEnabledAsync(string featureName, string scopeId) =>
        SomeFeatureApi.GetFeatureStatusForScope(scopeId);
}
```

And then you simply put your guards anywhere you want! 
They will automatically check if feature flag is enabled for a user in the scope of operation (say, an HTTP request in ASP.Net) when either method is invoked or property is set to non-default value, or simply do an inline check
```csharp
app.MapGet(
    "/preview",
    [Engarde("new-experience")] () => Results.Ok());

[Engarde("new-experience")]
public sealed class FeatureService
{
    [Engarde("operation-enabled")]
    public Task RunAsync() => Task.CompletedTask;

    [Engarde("editable-name")]
    public string? Name { get; set; }

    [Engarde("editable-state")]
    public string? State;

    public void Update([Engarde("valid-input")] string value) { }
}
```

```csharp
public sealed class NavigationService(IEngarde engarde)
{
    public ValueTask<bool> IsNewExperienceEnabledAsync() =>
        engarde.IsEnabledAsync("new-experience");
}
```

## Custom feature toggle names

`GenerateFeatureGuards` converts feature-name strings to PascalCase and generates a sealed, feature-bound guard and injectable interface for each name.

```csharp
[GenerateFeatureGuards("reports-view", "reports-edit")]
public partial class AccessGuard : EngardeAttribute
{
    protected override void OnFeatureMissing(
        string featureName,
        EngardeContext context) =>
        throw new UnauthorizedAccessException(featureName);
}

// Generated at compile time:
// sealed class ReportsViewAccessGuard : AccessGuard
// interface IReportsViewAccessGuard { ValueTask<bool> IsEnabledAsync(); }
// sealed class ReportsEditAccessGuard : AccessGuard
// interface IReportsEditAccessGuard { ValueTask<bool> IsEnabledAsync(); }

public sealed class ReportOperations
{
    [ReportsViewAccessGuard]
    public Task OpenAsync() => Task.CompletedTask;
}

public sealed class ReportMenu(IReportsViewAccessGuard reports)
{
    public ValueTask<bool> IsVisibleAsync() => reports.IsEnabledAsync();
}
```

## Define a custom guard manually

A regular guard receives a feature name at each use, while a feature-bound guard fixes its feature name, must be sealed, and exposes a parameterless generated query.

```csharp
public sealed partial class RuntimePermissionGuard(
    params string[] featureNames)
    : EngardeAttribute(featureNames)
{
    protected override void OnFeatureMissing(
        string featureName,
        EngardeContext context) =>
        throw new UnauthorizedAccessException(featureName);
}

public sealed class RuntimePermissionOperations
{
    [RuntimePermissionGuard("reports-view")]
    public Task OpenAsync() => Task.CompletedTask;
}

public sealed class RuntimePermissionQuery(
    IRuntimePermissionGuard permission)
{
    public ValueTask<bool> CanOpenAsync() =>
        permission.IsEnabledAsync("reports-view");
}
```

```csharp
public sealed partial class ReportsViewGuard()
    : EngardeAttribute("reports-view")
{
    protected override void OnFeatureMissing(
        string featureName,
        EngardeContext context) =>
        throw new UnauthorizedAccessException(featureName);
}

public sealed class ReportsViewOperations
{
    [ReportsViewGuard]
    public Task OpenAsync() => Task.CompletedTask;
}

public sealed class ReportsViewQuery(IReportsViewGuard permission)
{
    public ValueTask<bool> CanOpenAsync() =>
        permission.IsEnabledAsync();
}
```

## Custom feature toggle store

Derive from `EngardeAttribute<TStore>` to share custom store behavior and override `ResolveFeatureStore` in a child when it needs a different or fixed instance.

```csharp
public abstract class StoreBackedGuard(
    params string[] featureNames)
    : EngardeAttribute<AppFeatureStore>(featureNames)
{
    protected override ValueTask<bool> IsEnabledAsync(
        AppFeatureStore store,
        string featureName) =>
        store.IsEnabledAsync(featureName, "global");
}

public sealed partial class SearchGuard()
    : StoreBackedGuard("search");

public sealed partial class PreviewGuard()
    : StoreBackedGuard("preview")
{
    private static readonly AppFeatureStore Store = new();

    protected override AppFeatureStore ResolveFeatureStore(
        IServiceProvider services) =>
        Store;
}
```

## Custom scope ID

Derive from `EngardeAttribute<TScopeProvider, TScopeId>` to share scope resolution and override `GetFeatureScopeIdAsync` in a child when its scope differs.

```csharp
public abstract class TenantScopedGuard(
    params string[] featureNames)
    : EngardeAttribute<CurrentTenant, string>(featureNames)
{
    protected override ValueTask<string> GetFeatureScopeIdAsync(
        CurrentTenant tenant) =>
        ValueTask.FromResult(tenant.Id);
}

public sealed partial class TenantSettingsGuard()
    : TenantScopedGuard("tenant-settings");

public sealed partial class SharedSettingsGuard()
    : TenantScopedGuard("shared-settings")
{
    protected override ValueTask<string> GetFeatureScopeIdAsync(
        CurrentTenant tenant) =>
        ValueTask.FromResult($"shared:{tenant.Id}");
}
```

## .NET Core DI setup

`AddEngarde` registers `IEngarde` and every compile-time generated guard interface automatically as scoped services unless `UseQueryLifetime` selects another lifetime.

```csharp
builder.Services.AddSingleton<AppFeatureStore>();
builder.Services.AddScoped<CurrentTenant>();
builder.Services.AddEngarde(engarde => engarde
    .UseFeatureStore<AppFeatureStore, CurrentTenant, string>(
        tenant => ValueTask.FromResult(tenant.Id),
        (store, featureName, tenantId) =>
            store.IsEnabledAsync(featureName, tenantId))
    .OnFeatureMissing((featureName, _) =>
        throw new InvalidOperationException(featureName)));

public sealed class ReportService(
    IEngarde engarde,
    IReportsViewAccessGuard reportsView,
    IRuntimePermissionGuard runtimePermission)
{
    public ValueTask<bool> IsAnyReportEntryEnabledAsync() =>
        reportsView.IsEnabledAsync();
}
```

```csharp
builder.Services.AddEngarde(engarde => engarde
    .UseFeatureStore<AppFeatureStore>(
        (store, featureName) =>
            store.IsEnabledAsync(featureName, "global"))
    .OnFeatureMissing((featureName, _) =>
        throw new InvalidOperationException(featureName))
    .UseQueryLifetime(ServiceLifetime.Transient));
```

## DI in legacy apps

For a .NET Framework 4.7.2-style application, populate Autofac from `IServiceCollection`, activate Engarde once, and borrow each Autofac lifetime scope for woven and inline checks.

```xml
<package id="Autofac" version="6.5.0" targetFramework="net472" />
<package id="Autofac.Extensions.DependencyInjection" version="8.0.0" targetFramework="net472" />
<package id="Engarde" version="1.0.0" targetFramework="net472" />
```

```csharp
using Autofac;
using Autofac.Extensions.DependencyInjection;
using Engarde;
using Microsoft.Extensions.DependencyInjection;
using Guard = Engarde.Engarde;

public static class Bootstrapper
{
    public static async Task RunAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<AppFeatureStore>();
        services.AddScoped<LegacyTenant>();
        services.AddEngarde(engarde => engarde
            .UseFeatureStore<AppFeatureStore, LegacyTenant, string>(
                tenant => ValueTask.FromResult(tenant.Id),
                (store, featureName, tenantId) =>
                    store.IsEnabledAsync(featureName, tenantId))
            .OnFeatureMissing((featureName, _) =>
                throw new InvalidOperationException(featureName)));

        var builder = new ContainerBuilder();
        builder.Populate(services);

        using (var container = builder.Build())
        {
            var rootServices = new AutofacServiceProvider(container);
            Guard.Activate(rootServices);

            using (var lifetime = container.BeginLifetimeScope())
            {
                var requestServices = new AutofacServiceProvider(lifetime);
                using (Guard.BeginScope(requestServices, createScope: false))
                using (Guard.BeginEvaluationScope(requestServices))
                {
                    var engarde = requestServices.GetRequiredService<IEngarde>();
                    var reports = requestServices.GetRequiredService<IReportsViewAccessGuard>();

                    bool first = await engarde.IsEnabledAsync("new-experience");
                    bool second = await reports.IsEnabledAsync();
                }
            }
        }
    }
}

public sealed class LegacyTenant
{
    public string Id => Thread.CurrentPrincipal?.Identity?.Name ?? "anonymous";
}
```

## How to test

`Engarde.Testing` generates class and method overrides for all guards, with optional feature-name filters and inherited test-class behavior.

```sh
dotnet add package Engarde.Testing
```

```csharp
using Engarde.Testing;

[DisableAllEngarde]
public abstract class UnitTestBase;

public sealed class ServiceTests : UnitTestBase
{
    [Fact]
    public Task Runs_without_guards() =>
        service.RunAsync();
}
```

```csharp
[DisableAllEngarde]
public abstract class DisabledGuardTests;

[EnableAllEngarde("new-experience")]
public sealed class NewExperienceTests : DisabledGuardTests;

[DisableIRuntimePermissionGuard("reports-edit")]
public sealed class ReportsEditTests;

public sealed class MethodOverrideTests
{
    [DisableEngarde("operation-enabled")]
    [Fact]
    public Task Runs_without_the_selected_default_guard() =>
        service.RunAsync();
}
```
