namespace Engarde;

/// <summary>
/// Generates one sealed, feature-bound guard subclass per feature name. Apply only to a partial,
/// non-sealed guard template.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class GenerateFeatureGuardsAttribute(params string[] featureNames) : Attribute
{
    public IReadOnlyList<string> FeatureNames { get; } = featureNames;
}
