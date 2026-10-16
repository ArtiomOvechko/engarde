using Engarde.SampleWebApi.Guards;
using Microsoft.AspNetCore.Mvc;

namespace Engarde.SampleWebApi.Controllers;

[ApiController]
[Route("api/weatherforecast")]
public sealed class WeatherForecastController(
    IEngarde engarde,
    IEditWeatherGuard guard,
    IWeatherPermissionGuard weatherPermissionGuard) : ControllerBase
{
    private static string[] _summaries =
    [
        "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
    ];

    [HttpGet]
    [Engarde("weatherforecast_new")]
    public WeatherForecast[] Get()
    {
        return Enumerable.Range(1, 5).Select(index =>
            new WeatherForecast(
                DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
                Random.Shared.Next(-20, 55),
                _summaries[Random.Shared.Next(_summaries.Length)]))
            .ToArray();
    }
    
    [HttpPost]
    public async Task<ActionResult<string[]>> Post([FromBody] WeatherDto weatherDto)
    {
        if (
            !await engarde.IsEnabledAsync("weatherforecast") ||
            !await weatherPermissionGuard.IsEnabledAsync())
        {
            return Ok(Array.Empty<string>());
        }

        if (weatherDto.Overwrite)
            return _summaries = weatherDto.Names;
        
        return Ok(_summaries = [.. _summaries, .. weatherDto.Names]);
    }
}

public record WeatherDto
{
    public string[] Names { get; set; }
    
    [EditWeatherGuard]
    public bool Overwrite { get; set; }
}
