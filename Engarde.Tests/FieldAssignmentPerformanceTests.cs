using System.Diagnostics;
using System.Globalization;
using Engarde;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Xunit;
using Xunit.Abstractions;
using Guard = Engarde.Engarde;

namespace Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FieldAssignmentPerformanceCollection
{
    public const string Name = "Field assignment performance";
}

[Collection(FieldAssignmentPerformanceCollection.Name)]
public sealed class FieldAssignmentPerformanceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1_000_000, 3)]
    [InlineData(1, 0.01)]
    public async Task TestPerformance(int iterations, double allowedGuardedTimeInSeconds)
    {
        // Run without xUnit's synchronization context, which would dispatch every synchronous guard to Task.Run.
        var timings = await Task.Run(() => Compare(iterations));
        output.WriteLine($"Direct configuration check: {Seconds(timings.Direct)} s");
        output.WriteLine($"Guarded field assignment:   {Seconds(timings.Guarded)} s");
        output.WriteLine($"Guarded minus direct:      {Seconds(timings.Guarded - timings.Direct)} s");
        
        Assert.True(timings.Guarded < allowedGuardedTimeInSeconds);
    }

    private static (double Direct, double Guarded) Compare(int iterations)
    {
        var configurationRoot = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [FieldFeatureGuardAttribute.FeatureFlagName] = "true" }).Build();
        using var configurationOwner = (IDisposable)configurationRoot;
        var configuration = new CountingConfiguration(configurationRoot);
        using var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddEngarde(builder => builder
                .UseFeatureStore<IConfiguration>((store, featureName) => ValueTask.FromResult(
                    bool.TryParse(store[featureName], out var enabled) && enabled))
                .OnFeatureMissing((featureName, _) =>
                    throw new InvalidOperationException($"Feature '{featureName}' is disabled.")))
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var active = Guard.BeginScope(services);

        long started;
        
        CollectGarbage();
        started = Stopwatch.GetTimestamp();
        RunGuarded(iterations);
        var guardedSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        
        CollectGarbage();
        started = Stopwatch.GetTimestamp();
        RunDirect(configuration, iterations);
        var directSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        
        return (directSeconds, guardedSeconds);
    }
    
    private static void RunDirect(IConfiguration configuration, int iterations)
    {
        for (var i = 1; i <= iterations; i++)
        {
            if (!bool.TryParse(configuration[FieldFeatureGuardAttribute.FeatureFlagName], out var enabled) || !enabled)
                throw new InvalidOperationException("The feature flag must be enabled.");
            _ = new UnguardedValue { Amount = i };
        }
    }
    
    private static void RunGuarded(int iterations)
    {
        for (var i = 1; i <= iterations; i++)
        {
            _ = new GuardedValue { Amount = i };
        }
    }

    private static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static string Seconds(double seconds) => seconds.ToString("F3", CultureInfo.InvariantCulture);

    private sealed class UnguardedValue
    {
        public int Amount;
    }

    private sealed class GuardedValue
    {
        [FieldFeatureGuard] public int Amount;
    }

    private sealed class FieldFeatureGuardAttribute() : EngardeAttribute<IConfiguration>(FeatureFlagName)
    {
        public const string FeatureFlagName = "FeatureManagement:FieldAssignment";

        protected override ValueTask<bool> IsEnabledAsync(IConfiguration store, string featureName) =>
            ValueTask.FromResult(bool.TryParse(store[featureName], out var enabled) && enabled);
    }

    private sealed class CountingConfiguration(IConfiguration inner) : IConfiguration
    {
        public string? this[string key]
        {
            get => inner[key];
            set => inner[key] = value;
        }
        public IEnumerable<IConfigurationSection> GetChildren() => inner.GetChildren();
        public IChangeToken GetReloadToken() => inner.GetReloadToken();
        public IConfigurationSection GetSection(string key) => inner.GetSection(key);
    }
}
