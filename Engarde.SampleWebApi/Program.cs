using Engarde;
using Engarde.SampleWebApi;
using Engarde.SampleWebApi.Guards;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services
    .AddOpenApi()
    .AddEngarde(engarde =>
    {
        engarde.UseFeatureStore<IConfiguration>((config, featureName)
            => ValueTask.FromResult(bool.TryParse(config.GetSection(featureName).Value, out var result) && result));

        engarde.OnFeatureMissing((featureName, context)
            => throw new InvalidOperationException($"{featureName} is required to use {context.Member.Name}"));
    });

builder.Services.AddControllers();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseEngarde();
app.MapControllers();

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", [Engarde("weatherforecast")]() =>
    {
        var forecast = Enumerable.Range(1, 5).Select(index =>
                new WeatherForecast
                (
                    DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
                    Random.Shared.Next(-20, 55),
                    summaries[Random.Shared.Next(summaries.Length)]
                ))
            .ToArray();
        return forecast;
    })
    .WithName("GetWeatherForecast");

app.Run();
