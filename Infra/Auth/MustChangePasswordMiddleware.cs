using ExamOps.BLL;

namespace ExamOps.Infra.Auth;

public static class MustChangePasswordMiddleware
{
    public static IApplicationBuilder UseMustChangePasswordRedirect(this IApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            if (context.User.Identity?.IsAuthenticated == true
                && context.Request.Path is { } path
                && !path.StartsWithSegments("/account/password")
                && !path.StartsWithSegments("/login")
                && !path.StartsWithSegments("/logout")
                && !path.StartsWithSegments("/health")
                && !path.StartsWithSegments("/Error")
                && !path.StartsWithSegments("/_blazor")
                && !path.StartsWithSegments("/_framework")
                && !path.StartsWithSegments("/css")
                && !path.StartsWithSegments("/js")
                && !path.StartsWithSegments("/img")
                && !path.StartsWithSegments("/fonts"))
            {
                var currentUser = context.RequestServices.GetRequiredService<ICurrentUser>();
                await currentUser.LoadAsync();
                if (currentUser.MustChangePassword)
                {
                    context.Response.Redirect("/account/password");
                    return;
                }
            }
            await next();
        });
}
