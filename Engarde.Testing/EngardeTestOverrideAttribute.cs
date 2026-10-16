namespace Engarde.Testing;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = true)]
public abstract class EngardeTestOverrideAttribute : Attribute
{
    protected EngardeTestOverrideAttribute(bool enabled, string[] featureNames, Type? guardType = null, bool includeDerived = false)
    {
        Enabled = enabled;
        FeatureNames = featureNames;
        GuardType = guardType;
        IncludeDerived = includeDerived;
    }

    protected EngardeTestOverrideAttribute(bool enabled, string guardTypeIdentity, bool includeDerived, string[] featureNames)
    {
        Enabled = enabled;
        FeatureNames = featureNames;
        GuardTypeIdentity = guardTypeIdentity;
        IncludeDerived = includeDerived;
    }

    internal bool Enabled { get; }
    internal Type? GuardType { get; }
    internal string? GuardTypeIdentity { get; }
    internal bool IncludeDerived { get; }
    internal IReadOnlyList<string> FeatureNames { get; }

    internal bool IsAll => GuardType is null && GuardTypeIdentity is null;

    internal bool Matches(Type guardType)
    {
        if (IsAll) return true;
        for (var current = guardType; current is not null; current = IncludeDerived ? current.BaseType : null)
        {
            if (GuardType == current || GuardTypeIdentity == Identity(current))
                return true;
        }
        return false;
    }

    internal bool MatchesFeature(string featureName) =>
        FeatureNames.Count == 0 || FeatureNames.Contains(featureName, StringComparer.Ordinal);

    private static string Identity(Type type)
    {
        if (type.IsConstructedGenericType) type = type.GetGenericTypeDefinition();
        return type.Assembly.GetName().Name + "|" + type.FullName;
    }
}

public sealed class DisableAllEngardeAttribute(params string[] featureNames) : EngardeTestOverrideAttribute(false, featureNames);
public sealed class EnableAllEngardeAttribute(params string[] featureNames) : EngardeTestOverrideAttribute(true, featureNames);
public sealed class DisableEngardeAttribute(params string[] featureNames) : EngardeTestOverrideAttribute(false, featureNames, typeof(EngardeAttribute));
public sealed class EnableEngardeAttribute(params string[] featureNames) : EngardeTestOverrideAttribute(true, featureNames, typeof(EngardeAttribute));
