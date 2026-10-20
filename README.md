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

Method fallbacks
```csharp
public sealed class Checkout
{
    [Engarde(nameof(PlaceOrderWithoutDiscount), typeof(Checkout), "discount-checkout")]
    public Task<Order> PlaceOrderAsync(Cart cart) => PlaceDiscountedOrderAsync(cart);

    private Task<Order> PlaceOrderWithoutDiscount(Cart cart) => PlaceRegularOrderAsync(cart);
}
```

DI-backed type fallbacks
```csharp
[Engarde(typeof(LegacyWeatherService), "new-weather-service")]
public interface IWeatherService
{
    Task<Forecast> GetForecastAsync();
}

public sealed class NewWeatherService : IWeatherService
{
    public Task<Forecast> GetForecastAsync() => GetNewForecastAsync();
}

public sealed class LegacyWeatherService : IWeatherService
{
    public Task<Forecast> GetForecastAsync() => GetLegacyForecastAsync();
}

// Register primary implementation in DI
// AddEngarde will automatically register fallback with the same scoped lifetime.
builder.Services.AddScoped<IWeatherService, NewWeatherService>();
```

Static methods can be guarded like instance methods.

```csharp
public static class ReportExporter
{
    [Engarde("pdf-export")]
    public static Task<byte[]> ExportPdfAsync(Report report) => RenderPdfAsync(report);
}
```

Value-based guards

```csharp
public enum PizzaType
{
    Mushroom,
    Pineapple
}

public sealed class PizzaOrder
{
    [Engarde(PizzaType.Pineapple, "new-flavor")]
    public PizzaType? Type { get; set; }

    public void ChangeType(
        [Engarde(PizzaType.Pineapple, "new-flavor")] PizzaType type)
    {
        Type = type;
    }
}
```

## Setup

Register a feature store and, optionally, how to resolve the scope ID (here, the current user) that features are checked for.

```csharp
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddSingleton<FeatureStore>();
builder.Services.AddEngarde(engarde => engarde
    .UseFeatureStore<FeatureStore, CurrentUser, string>(
        (store, featureName, userId) => store.IsEnabledAsync(featureName, userId),
        user => ValueTask.FromResult(user.Id))
    .OnFeatureMissing((featureName, context) =>
        throw new InvalidOperationException($"'{featureName}' is required by {context.Member.Name}")));

var app = builder.Build();
app.UseEngarde(); // checks use the request's services
```

Use `UseFeatureStore<FeatureStore>((store, featureName) => ...)` when features don't depend on a scope. Every check calls the store; nothing is cached.

`OnFeatureMissing` is optional. Without it nothing happens for a disabled feature: calls proceed, and property and field writes are skipped.

## Setup in apps without DI

Derive from `EngardeAttribute<TStore>` and override `ResolveFeatureStore` to use a store that isn't resolved from DI.
Guards that override both `IsEnabledAsync` and `OnFeatureMissing`, or return a fixed store, work without `AddEngarde`.

```csharp
public sealed partial class AppWithoutDiCustomGuard(params string[] featureNames)
    : EngardeAttribute<FeatureStore>(featureNames)
{
    protected override FeatureStore ResolveFeatureStore(IServiceProvider services) => new FeatureStore();

    protected override ValueTask<bool> IsEnabledAsync(FeatureStore store, string featureName, CancellationToken cancellationToken) =>
        store.IsEnabledAsync(featureName, "app");

    protected override void OnFeatureMissing(string featureName, EngardeContext context) =>
        throw new InvalidOperationException(featureName);
}
```

A guard is checked when a method or constructor is called, and when a property, field or parameter receives a non-empty value (not null, default, an empty string or an empty collection). A disabled feature calls `OnFeatureMissing`; throwing blocks the call, and a disabled property or field write is skipped.

## Inherited guards

Guards are inherited down the type hierarchy:

- An interface guard works like a class guard on every implementing class or struct: it is checked on construction and on every method call, including the interface's default implementations.
- A class guard also guards the methods of derived classes (it already ran in the base constructor).
- A method, property or parameter guard also guards the members that override or implement it.

```csharp
[Engarde("my-feature")]
public interface IMyFeature
{
    [Engarde("my-feature-run")]
    Task RunAsync([Engarde("my-feature-option")] int option);
}

[Engarde("my-feature-beta")]
public sealed class MyFeature : IMyFeature // constructing it checks "my-feature-beta" and "my-feature"
{
    // checks "my-feature-run", "my-feature-beta" and "my-feature", plus "my-feature-option" for a non-zero option
    public Task RunAsync(int option) => Task.CompletedTask;
}
```

Inherited guards add up: every guard along the hierarchy runs, in addition to the member's and type's own, even when several guards name the same feature.

When a class re-declares an interface whose members are implemented by methods inherited from a base class that doesn't implement it, Engarde adds an explicit implementation to the class. A call through the interface checks the interface member's guards, then calls the inherited method, which checks its own member and type guards.

## Inline checks

```csharp
public sealed class Navigation(IEngarde engarde)
{
    public ValueTask<bool> ShowPreviewAsync() => engarde.IsFeatureEnabledAsync("new-experience");
}
```

`IEngarde` only reports the status and never calls `OnFeatureMissing`.

Every `IsFeatureEnabledAsync`, including those of generated guard interfaces, has an overload taking a `CancellationToken`. Custom guards receive it in the async methods they override (`IsEnabledAsync`, `GetScopeIdAsync`); attribute checks pass `CancellationToken.None`.

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

A guard's generated query calls `OnFeatureMissing` for each disabled feature; add `[SkipInlineFailures]` to return the Boolean result without calling it, while woven attribute checks still call it.

```csharp
[SkipInlineFailures]
public sealed partial class MenuGuard() : EngardeAttribute("menu");
```

### Failures before fallbacks

A valid method or type fallback calls `OnFeatureMissing` before dispatch by default, so throwing prevents the fallback; add `[SkipFallbackFailures]` to the custom guard class to dispatch without calling it.

```csharp
[SkipFallbackFailures]
public sealed partial class FallbackMenuGuard(params string[] featureNames)
    : EngardeAttribute(featureNames);

public sealed class Menu
{
    [FallbackMenuGuard(nameof(OpenLegacy), typeof(Menu), "new-menu")]
    public void Open() => OpenNew();

    private void OpenLegacy() => OpenOld();
}
```

## Custom scope

```csharp
public sealed partial class UserGuard(params string[] featureNames)
    : EngardeAttribute<CurrentUser, string>(featureNames)
{
    protected override ValueTask<string> GetScopeIdAsync(CurrentUser user, CancellationToken cancellationToken) =>
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

Each guard gets `Enable<Guard>` and `Disable<Guard>` attributes for test classes and methods, optionally filtered by feature names. They are generated in the `Engarde.Testing` namespace, or in the guard's namespace when two guards share a name. They also cover the test class constructor and field initializers, so services created there are checked with the same overrides.
