# Engarde

Feature-flag guards for methods, constructors, types, properties, fields and parameters, plus generated injectable queries for inline checks.

```sh
dotnet add package En-Garde
```

## Examples

```csharp
[Engarde("new-experience-enabled")]
public sealed class MyService
{
    [MyCustomGuardThatThrows("beta-testers")]
    public Task NewMethod();

    [AllowEditingNameGuard]
    public string? Name { get; set; }

    public void Update(string field, string value, [Engarde("version-update-allowed")] int version) { }
}
```

## Setup

Register a feature store and, optionally, how to resolve the scope ID (here, the current user) that features are checked for.

```csharp
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddSingleton<FeatureStore>();
builder.Services.AddEngarde(engarde => engarde
    .UseFeatureStore<FeatureStore, CurrentUser, string>(
        user => ValueTask.FromResult(user.Id),
        (store, featureName, userId) => store.IsEnabledAsync(featureName, userId))
    .OnFeatureMissing((featureName, context) =>
        throw new InvalidOperationException($"'{featureName}' is required by {context.Member.Name}")));

var app = builder.Build();
app.UseEngarde(); // checks use the request's services
```

Use `UseFeatureStore<FeatureStore>((store, featureName) => ...)` when features don't depend on a scope. Every check calls the store; nothing is cached.

## Setup in apps without DI

Derive from `EngardeAttribute<TStore>` and override `ResolveFeatureStore` to use a store that isn't resolved from DI.
Guards that override both `IsEnabledAsync` and `OnFeatureMissing`, or return a fixed store, work without `AddEngarde`.

```csharp
public sealed partial class AppWithoutDiCustomGuard(params string[] featureNames)
    : EngardeAttribute<FeatureStore>(featureNames)
{
    protected override FeatureStore ResolveFeatureStore(IServiceProvider services) => new FeatureStore();

    protected override ValueTask<bool> IsEnabledAsync(FeatureStore store, string featureName) =>
        store.IsEnabledAsync(featureName, "app");

    protected override void OnFeatureMissing(string featureName, EngardeContext context) =>
        throw new InvalidOperationException(featureName);
}
```

A guard is checked when a method or constructor is called, and when a property, field or parameter receives a non-empty value (not null, default, an empty string or an empty collection). A disabled feature calls `OnFeatureMissing`; throwing blocks the call, and a disabled property or field write is skipped.

## Inline checks

```csharp
public sealed class Navigation(IEngarde engarde)
{
    public ValueTask<bool> ShowPreviewAsync() => engarde.IsFeatureEnabledAsync("new-experience");
}
```

`IEngarde` only reports the status and never calls `OnFeatureMissing`.

## Custom guards

A partial guard gets a generated interface, registered by `AddEngarde`, for checking it inline.

```csharp
public sealed partial class ReportsGuard() : EngardeAttribute("reports")
{
    protected override void OnFeatureMissing(string featureName, EngardeContext context) =>
        throw new UnauthorizedAccessException(featureName);
}

public sealed class Reports
{
    [ReportsGuard]
    public Task OpenAsync() => Task.CompletedTask;
}

public sealed class ReportsMenu(IReportsGuard reports)
{
    public ValueTask<bool> IsVisibleAsync() => reports.IsFeatureEnabledAsync();
}
```

A guard that takes feature names, such as `PermissionGuard(params string[] featureNames)`, gets `IsFeatureEnabledAsync(string featureFlagName)` instead. A guard with a fixed feature name must be sealed.

`GenerateFeatureGuards` creates one sealed guard and interface per feature name:

```csharp
[GenerateFeatureGuards("reports-view", "reports-edit")]
public partial class AccessGuard : EngardeAttribute;

//Generated: ReportsViewAccessGuard / IReportsViewAccessGuard, ReportsEditAccessGuard / IReportsEditAccessGuard 
// that automatically registered in DI if UseEngarde setup
```

### Failures in inline checks

A guard's generated query calls its `OnFeatureMissing` for each disabled feature. Add `[SkipInlineFailures]` to return boolean result without calling OnFeatureMissing(); attribute guards still call it

```csharp
[SkipInlineFailures]
public sealed partial class MenuGuard() : EngardeAttribute("menu");
```

## Custom scope

```csharp
public sealed partial class UserGuard(params string[] featureNames)
    : EngardeAttribute<CurrentUser, string>(featureNames)
{
    protected override ValueTask<string> GetFeatureScopeIdAsync(CurrentUser user) =>
        ValueTask.FromResult(user.Id);
}
```

A guard with its own scope ID type also gets a query overload that takes the scope ID, to check another scope instead of the current one:

```csharp
public sealed class Admin(IUserGuard guard)
{
    public ValueTask<bool> CanExportAsync(string userId) => guard.IsFeatureEnabledAsync("export", userId);
}
```

Any guard can be checked for a scope ID with `Engarde.CreateInstance().IsFeatureEnabledAsync<TScopeId>(guard, scopeId)`; the type must match the guard's or the registered store's.

`IEngarde` and generated interfaces are scoped by default; change that with `.UseQueryLifetime(ServiceLifetime.Singleton)`. 

## Testing

```sh
dotnet add package En-Garde.Testing
```

```csharp
[DisableAllEngarde]
public abstract class UnitTestBase;

[EnableAllEngarde("new-experience")]
public sealed class NewExperienceTests : UnitTestBase
{
    [DisableReportsGuard]
    [Fact]
    public Task Opens_reports_without_the_guard() => reports.OpenAsync();
}
```

Each guard gets `Enable<Guard>` and `Disable<Guard>` attributes for test classes and methods, optionally filtered by feature names.
