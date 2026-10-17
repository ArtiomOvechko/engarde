namespace Microsoft.AspNetCore.Builder;

/// <summary>Request lifetime integration for Engarde feature evaluations.</summary>
public static class EngardeApplicationBuilderExtensions
{
    /// <summary>
    /// Evaluates features in the request with HttpContext.RequestServices.
    /// Place after authentication/tenant resolution and before guarded middleware or endpoints.
    /// The request services stop being used when the downstream pipeline finishes, including on exceptions;
    /// ASP.NET Core retains ownership of request services.
    /// </summary>
    public static IApplicationBuilder UseEngarde(this IApplicationBuilder app)
    {
        if (app is null) throw new ArgumentNullException(nameof(app));
        return app.Use(static next => async context =>
        {
            using var scope = global::Engarde.Engarde.BeginScope(context.RequestServices, createScope: false);
            await next(context).ConfigureAwait(false);
        });
    }
}
