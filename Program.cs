using ExamOps.BLL;
using ExamOps.Components;
using ExamOps.Infra;
using ExamOps.Infra.Auth;
using ExamOps.Infra.Import;
using ExamOps.Infra.Storage;
using ExamOps.Infra.Verification;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using System.Net;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 12 * 1024 * 1024);
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 12 * 1024 * 1024);
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    foreach (var proxy in builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [])
    {
        if (IPAddress.TryParse(proxy, out var address)) options.KnownProxies.Add(address);
    }
});

if (args.Contains("--import-parser-check"))
{
    ImportParserCheck.Run();
    return;
}

if (args.Contains("--self-check"))
{
    new SelfCheck(builder.Environment, new ExamOps.Model.Common.SystemClock()).Run();
    return;
}

builder.Services.AddInfrastructure(builder);
var app = builder.Build();

await app.Services.GetRequiredService<StartupValidator>().ValidateAsync();
var roots = ServiceCollectionExtensions.ResolveDataRoots(builder);
app.Logger.LogInformation("ExamOps schema 1.1.0 校验通过");
app.Logger.LogInformation("日志根目录 {LogRoot}", roots.LogRoot);
app.Logger.LogInformation("上传根目录 {UploadRoot}", roots.UploadRoot);
app.Logger.LogInformation("导出根目录 {ExportRoot}", roots.ExportRoot);
app.Logger.LogInformation("临时根目录 {TmpRoot}", roots.TmpRoot);

if (args.Contains("--phase1-check"))
{
    await app.Services.GetRequiredService<Phase1Check>().RunAsync();
    return;
}

if (args.Contains("--phase2-check"))
{
    await app.Services.GetRequiredService<Phase2Check>().RunAsync();
    return;
}

if (args.Contains("--phase3-check"))
{
    await app.Services.GetRequiredService<Phase3Check>().RunAsync();
    return;
}

if (args.Contains("--phase4-concurrency-check"))
{
    await app.Services.GetRequiredService<Phase4Check>().RunConcurrencyAsync();
    return;
}

if (args.Contains("--phase4-check"))
{
    await app.Services.GetRequiredService<Phase4Check>().RunAsync();
    return;
}

if (args.Contains("--phase5-check"))
{
    await app.Services.GetRequiredService<Phase5Check>().RunAsync();
    return;
}

if (args.Contains("--init-admin"))
{
    using var scope = app.Services.CreateScope();
    var result = await scope.ServiceProvider.GetRequiredService<AdminBootstrap>().InitializeAsync();
    if (result.IsSuccess) app.Logger.LogInformation("管理员初始化完成。");
    else
    {
        app.Logger.LogError("管理员初始化失败：{Error}", result.Error);
        Environment.ExitCode = 1;
    }
    return;
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
    app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseMustChangePasswordRedirect();
app.UseStaticFiles();
app.UseAntiforgery();
app.UseRateLimiter();

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapControllers();
app.MapPost("/logout", async (HttpContext httpContext, [FromForm] string? __RequestVerificationToken) =>
{
    await httpContext.RequestServices.GetRequiredService<AuthService>().LogoutAsync();
    await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
}).RequireAuthorization();

app.MapGet("/health", async (IFileStorage storage, StartupValidator validator, CancellationToken cancellationToken) =>
{
    bool db;
    try
    {
        await validator.ValidateAsync();
        db = true;
    }
    catch (Exception ex) when (ex is InvalidOperationException or Npgsql.PostgresException)
    {
        db = false;
    }
    var writable = storage.TryProbeWritable();
    return Results.Ok(new { status = db && writable ? "ok" : "degraded", db, writable });
}).AllowAnonymous();

app.Run();
