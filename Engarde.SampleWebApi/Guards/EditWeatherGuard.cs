namespace Engarde.SampleWebApi.Guards;

public sealed partial class EditWeatherGuard() : EngardeAttribute("weatherforecast_edit")
{
    protected override void OnFeatureMissing(string featureName, EngardeContext context)
    {
        throw new UnauthorizedAccessException($"{featureName} is disabled");
    }
}

[GenerateFeatureGuards("weather", "editweather")]
public partial class PermissionGuard : EngardeAttribute
{
    protected override void OnFeatureMissing(string featureName, EngardeContext context)
    {
        Console.WriteLine($"Feature is missing: {featureName}");
    }
}