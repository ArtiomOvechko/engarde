namespace Engarde;

/// <summary>
/// Fallbacks of this guard are dispatched without calling <see cref="EngardeAttribute.OnFeatureMissing"/>.
/// Guards without this attribute call the failure handler before invoking a valid method or type fallback.
/// Inherited by derived guards, including generated feature-bound guards.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class SkipFallbackFailuresAttribute : Attribute;
