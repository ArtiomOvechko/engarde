namespace Engarde;

/// <summary>
/// Inline queries of this guard (its generated interface or <see cref="Engarde.IsFeatureEnabledAsync(EngardeAttribute)"/>)
/// only report whether features are enabled, without calling <see cref="EngardeAttribute.OnFeatureMissing"/>.
/// Woven checks still call it. Inherited by derived guards, including generated feature-bound guards.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class SkipInlineFailuresAttribute : Attribute;
