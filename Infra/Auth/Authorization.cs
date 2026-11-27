using System.Security.Claims;
using ExamOps.BLL;
using ExamOps.DAL.Repositories;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;

namespace ExamOps.Infra.Auth;

public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

public sealed class PermissionPolicyProvider : IAuthorizationPolicyProvider
{
    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        var policy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(policyName))
            .Build();
        return Task.FromResult<AuthorizationPolicy?>(policy);
    }

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync()
        => Task.FromResult(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => Task.FromResult<AuthorizationPolicy?>(null);
}

public sealed class PermissionAuthorizationHandler(ICurrentUser currentUser) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        await currentUser.LoadAsync();
        if (context.User.Identity?.IsAuthenticated != true || !currentUser.IsAuthenticated)
            return;
        if (currentUser.HasPermission(requirement.Permission))
            context.Succeed(requirement);
    }
}

public static class CookieAuthenticationSetup
{
    public static IServiceCollection AddExamOpsCookieAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "ExamOps.Auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.LoginPath = "/login";
                options.LogoutPath = "/logout";
                options.AccessDeniedPath = "/forbidden";
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;
                options.Events.OnRedirectToLogin = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/api"))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    }
                    var returnUrl = context.Request.Path + context.Request.QueryString;
                    context.Response.Redirect($"/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.Redirect("/forbidden");
                    return Task.CompletedTask;
                };
                options.Events.OnValidatePrincipal = async context =>
                {
                    if (!long.TryParse(context.Principal?.FindFirstValue("examops_user_id"), out var userId))
                    {
                        context.RejectPrincipal();
                        return;
                    }
                    var repository = context.HttpContext.RequestServices.GetRequiredService<AccessRepository>();
                    var stamp = await repository.GetStampAsync(userId);
                    var expectedVersion = context.Principal?.FindFirstValue("examops_row_version");
                    if (stamp is null || stamp.Status != "Active" || stamp.RowVersion.ToString() != expectedVersion)
                    {
                        context.RejectPrincipal();
                        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    }
                };
            });
        return services;
    }
}
