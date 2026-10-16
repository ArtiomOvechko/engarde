namespace Microsoft.AspNetCore.Builder;

/// <summary>Request lifetime integration for Engarde feature evaluations.</summary>
public static class EngardeApplicationBuilderExtensions
{
    /// <summary>
    /// Shares feature evaluations across the request using HttpContext.RequestServices.
    /// Place after authentication/tenant resolution and before guarded middleware or endpoints.
    /// The evaluation cache ends when the downstream pipeline finishes, including on exceptions;
    /// ASP.NET Core retains ownership of request services.
    /// </summary>
    public static IApplicationBuilder UseEngarde(this IApplicationBuilder app)
    {
        if (app is null) throw new ArgumentNullException(nameof(app));
        return app.Use(static next => async context =>
        {
            using var evaluation = global::Engarde.Engarde.BeginEvaluationScope(context.RequestServices);
            await next(context).ConfigureAwait(false);
        });
    }
}
