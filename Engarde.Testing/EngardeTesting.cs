using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;

namespace Engarde.Testing;

public static class EngardeTesting
{
    private static readonly AsyncLocal<OverridePlan?> Current = new();
    private static readonly ConcurrentDictionary<(nint Method, nint Type), OverridePlan> Plans = new();
    private static int _activated;

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void Activate()
    {
        if (Interlocked.Exchange(ref _activated, 1) == 0)
            EngardeChecks.SetGuardOverride(GetOverride);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static IDisposable Enter(RuntimeMethodHandle method, RuntimeTypeHandle type)
    {
        Activate();
        var previous = Current.Value;
        Current.Value = Plans.GetOrAdd((method.Value, type.Value), static (_, handles) =>
            CreatePlan(MethodBase.GetMethodFromHandle(handles.method, handles.type)!), (method, type));
        return new Scope(previous);
    }

    private static bool? GetOverride(Type guardType, string featureName) => Current.Value?.GetOverride(guardType, featureName);

    private static OverridePlan CreatePlan(MethodBase method)
    {
        var scopes = new List<EngardeTestOverrideAttribute[]>();
        var hierarchy = new Stack<Type>();
        for (var current = method.DeclaringType; current is not null; current = current.BaseType)
            hierarchy.Push(current);
        foreach (var current in hierarchy)
            scopes.Add(current.GetCustomAttributes<EngardeTestOverrideAttribute>(false).ToArray());
        scopes.Add(method.GetCustomAttributes<EngardeTestOverrideAttribute>(false).ToArray());
        return new(scopes.ToArray());
    }

    private sealed class OverridePlan(EngardeTestOverrideAttribute[][] scopes)
    {
        internal bool? GetOverride(Type guardType, string featureName)
        {
            bool? result = null;
            foreach (var attributes in scopes)
            {
                var all = attributes.LastOrDefault(attribute => attribute.IsAll && attribute.MatchesFeature(featureName));
                if (all is not null)
                    result = all.GuardResult;
                var specific = attributes.LastOrDefault(attribute => !attribute.IsAll
                    && attribute.Matches(guardType) && attribute.MatchesFeature(featureName));
                if (specific is not null)
                    result = specific.GuardResult;
            }
            return result;
        }
    }

    private sealed class Scope(OverridePlan? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Current.Value = previous;
        }
    }
}
