using Npgsql;

namespace ExamOps.Infra.Auth;

/// <summary>
/// P0-2: Enforces production secret hygiene on startup so placeholder passwords,
/// superuser roles, or missing env-injected credentials fail fast before serving traffic.
/// </summary>
public static class SecretConfigValidator
{
    public static string ResolveConnectionString(IConfiguration configuration, IHostEnvironment environment)
    {
        var envOverride = Environment.GetEnvironmentVariable("EXAMOPS_DB_CONNECTION");
        var connection = !string.IsNullOrWhiteSpace(envOverride)
            ? envOverride.Trim()
            : configuration.GetConnectionString("App");

        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("缺少数据库连接串：请通过环境变量 EXAMOPS_DB_CONNECTION 或 ConnectionStrings__App 注入。");

        if (!environment.IsDevelopment())
            ValidateProductionConnectionString(connection);

        return connection;
    }

    public static void ValidateProductionConnectionString(string connectionString)
    {
        NpgsqlConnectionStringBuilder csb;
        try
        {
            csb = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("生产数据库连接串格式无效，拒绝启动。", ex);
        }

        if (string.IsNullOrWhiteSpace(csb.Username) || string.Equals(csb.Username, "postgres", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("生产环境禁止使用 postgres 超级用户连接数据库，请使用最小权限角色（如 examops_app）。");

        if (string.IsNullOrWhiteSpace(csb.Password)
            || csb.Password.Contains('<')
            || csb.Password.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase)
            || csb.Password.Contains("数据库密码", StringComparison.Ordinal))
            throw new InvalidOperationException("生产数据库密码未正确通过环境变量或受控密钥注入（检测到空值或占位符），拒绝启动。");
    }
}
