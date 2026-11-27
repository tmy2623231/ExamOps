using ExamOps.Api;
using ExamOps.BLL;
using ExamOps.DAL;
using ExamOps.DAL.Repositories;
using ExamOps.Infra.Auth;
using ExamOps.Infra.Background;
using ExamOps.Infra.Logging;
using ExamOps.Infra.Storage;
using ExamOps.Infra.Verification;
using ExamOps.Model.Common;
using ExamOps.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql.EntityFrameworkCore.PostgreSQL;
using System.Threading.RateLimiting;

namespace ExamOps.Infra;
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, WebApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        var environment = builder.Environment;
        var connection = SecretConfigValidator.ResolveConnectionString(configuration, environment);
        var logRoot = PathResolver.ResolveRoot(environment.ContentRootPath, configuration["LoggingRoot"], "Data/logs");
        var uploadRoot = PathResolver.ResolveRoot(environment.ContentRootPath, configuration["Storage:UploadRoot"], "Data/uploads");
        var exportRoot = PathResolver.ResolveRoot(environment.ContentRootPath, configuration["Storage:ExportRoot"], "Data/export");
        var tmpRoot = PathResolver.ResolveRoot(environment.ContentRootPath, configuration["Storage:TmpRoot"], "Data/tmp");
        foreach (var root in new[] { logRoot, uploadRoot, exportRoot, tmpRoot }) Directory.CreateDirectory(root);

        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddMvc(options =>
        {
            options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
        }).ConfigureApiBehaviorOptions(options =>
        {
            options.InvalidModelStateResponseFactory = context => new BadRequestObjectResult(
                ApiEnvelope<object>.Fail("请求参数无效", "VALIDATION_ERROR"));
        });
        services.AddCascadingAuthenticationState();
        services.AddHttpContextAccessor();
        services.AddDbContextFactory<ExamDbContext>(options => options.UseNpgsql(connection, npgsql =>
        {
            npgsql.EnableRetryOnFailure(3);
            npgsql.CommandTimeout(30);
        }));
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<LoginThrottleService>();
        services.AddSingleton<UserSessionRegistry>();
        services.AddSingleton<IUserSessionInvalidator>(sp => sp.GetRequiredService<UserSessionRegistry>());
        services.AddSingleton<PasswordHasher<AppUser>>();
        services.AddSingleton<IFileStorage>(sp => new FileStorage(uploadRoot, tmpRoot, exportRoot, sp.GetRequiredService<IClock>()));
        services.AddSingleton<StartupValidator>();
        services.AddSingleton<IHostedService>(sp => new DataCleanupService(
            logRoot, exportRoot, tmpRoot,
            configuration.GetValue("Retention:LogDays", 30),
            configuration.GetValue("Retention:ExportHours", 24),
            configuration.GetValue("Retention:TmpHours", 1),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogger<DataCleanupService>>()));
        services.AddSingleton<ILoggerProvider>(sp => new DailyFileLoggerProvider(logRoot, sp.GetRequiredService<IClock>()));
        services.AddHostedService<ExamBackgroundService>();

        services.AddScoped<QuestionRepository>();
        services.AddScoped<PaperRepository>();
        services.AddScoped<ImportRepository>();
        services.AddScoped<QuestionService>();
        services.AddScoped<PaperService>();
        services.AddScoped<QuestionImportService>();
        services.AddScoped<DepartmentRepository>();
        services.AddScoped<UserRepository>();
        services.AddScoped<RoleRepository>();
        services.AddScoped<AccessRepository>();
        services.AddScoped<AuditRepository>();
        services.AddScoped<ExamTaskRepository>();
        services.AddScoped<AssignmentRepository>();
        services.AddScoped<AttemptRepository>();
        services.AddScoped<ExamReviewRepository>();
        services.AddScoped<ReportRepository>();
        services.AddScoped<ReportService>(sp => new ReportService(
            sp.GetRequiredService<IUnitOfWork>(), sp.GetRequiredService<ReportRepository>(),
            sp.GetRequiredService<AuditRepository>(), sp.GetRequiredService<OutboxRepository>(),
            sp.GetRequiredService<ExamIdempotencyService>(), sp.GetRequiredService<DataScopeService>(),
            sp.GetRequiredService<ICurrentUser>(), sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<IFileStorage>(),
            TimeSpan.FromHours(configuration.GetValue("Retention:ExportHours", 24)),
            sp.GetRequiredService<ILogger<ReportService>>()));
        services.AddScoped<OutboxRepository>();
        services.AddScoped<ExamIdempotencyService>();
        services.AddScoped<ExamTaskService>();
        services.AddScoped<AssignmentService>();
        services.AddScoped<ExamAttemptService>();
        services.AddScoped<ExamReviewService>();
        services.AddScoped<ResultReleaseService>();
        services.AddScoped<ExamMaintenanceService>();
        services.AddScoped<OutboxConsumerService>();
        services.AddScoped<ICurrentUser, CurrentUserService>();
        services.AddScoped<ICurrentRequestContext, HttpCurrentRequestContext>();
        services.AddScoped<DataScopeService>();
        services.AddScoped<OrganizationService>();
        services.AddScoped<UserService>();
        services.AddScoped<RoleService>();
        services.AddScoped<AuthService>();
        services.AddScoped<AdminBootstrap>();
        services.AddScoped<DashboardRepository>();
        services.AddScoped<DashboardService>();
        services.AddScoped<QuickPublishService>();
        services.AddScoped<ExamOps.Infra.Ui.ToastService>();
        services.AddSingleton<SelfCheck>();
        services.AddSingleton<Phase1Check>();
        services.AddSingleton<Phase2Check>();
        services.AddSingleton<Phase3Check>();
        services.AddSingleton<Phase4Check>();
        services.AddSingleton<Phase5Check>();

        services.AddExamOpsCookieAuthentication(!builder.Environment.IsDevelopment());
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));
            options.AddPolicy("import", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));
        });
        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        });
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        return services;
    }

    public static (string LogRoot, string UploadRoot, string ExportRoot, string TmpRoot) ResolveDataRoots(WebApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        var root = builder.Environment.ContentRootPath;
        return (
            PathResolver.ResolveRoot(root, configuration["LoggingRoot"], "Data/logs"),
            PathResolver.ResolveRoot(root, configuration["Storage:UploadRoot"], "Data/uploads"),
            PathResolver.ResolveRoot(root, configuration["Storage:ExportRoot"], "Data/export"),
            PathResolver.ResolveRoot(root, configuration["Storage:TmpRoot"], "Data/tmp"));
    }
}
