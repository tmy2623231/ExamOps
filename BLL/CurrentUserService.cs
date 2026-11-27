using System.Security.Claims;
using ExamOps.DAL.Repositories;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;

namespace ExamOps.BLL;

public interface ICurrentUser
{
    long? Id { get; }
    string? DisplayName { get; }
    string? Username { get; }
    bool IsAuthenticated { get; }
    bool MustChangePassword { get; }
    Task LoadAsync();
    bool HasPermission(string permission);
    bool HasAnyPermission(params string[] permissions);
    bool HasRole(string roleCode);
    IReadOnlyList<RoleAssignmentData> Roles { get; }
    IReadOnlyList<DepartmentAccessData> Departments { get; }
    AuditContext GetAuditContext();
}

public sealed class CurrentUserService : ICurrentUser
{
    private readonly Func<long, Task<CurrentUserData?>> _getData;
    private CurrentUserData? _data;
    private Task? _loadTask;

    public CurrentUserService(AccessRepository repository, IHttpContextAccessor httpContextAccessor)
        : this(userId => repository.GetAsync(userId), httpContextAccessor)
    {
    }

    internal CurrentUserService(Func<long, Task<CurrentUserData?>> getData, IHttpContextAccessor httpContextAccessor)
    {
        _getData = getData;
        HttpContextAccessor = httpContextAccessor;
    }

    private IHttpContextAccessor HttpContextAccessor { get; }

    public long? Id => long.TryParse(HttpContextAccessor.HttpContext?.User.FindFirstValue("examops_user_id"), out var id) ? id : null;
    public string? DisplayName => _data?.DisplayName ?? HttpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.Name);
    public string? Username => _data?.Username ?? HttpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
    public bool IsAuthenticated => Id.HasValue;
    public bool MustChangePassword => _data?.MustChangePassword ?? false;
    public IReadOnlyList<RoleAssignmentData> Roles => _data?.Roles ?? [];
    public IReadOnlyList<DepartmentAccessData> Departments => _data?.Departments ?? [];

    public async Task LoadAsync()
    {
        if (_loadTask is not null)
        {
            await _loadTask;
            return;
        }

        var loadTask = LoadCoreAsync();
        _loadTask = loadTask;
        try
        {
            await loadTask;
        }
        finally
        {
            if (ReferenceEquals(_loadTask, loadTask)) _loadTask = null;
        }
    }

    private async Task LoadCoreAsync()
    {
        if (Id is not { } userId) return;
        var data = await _getData(userId);
        _data = data?.Status == "Active"
            ? data
            : data is null ? null : data with { Permissions = [], Roles = [], Departments = [] };
    }

    public bool HasPermission(string permission)
    {
        if (_data is null) throw new InvalidOperationException("CurrentUser.LoadAsync must be called first.");
        return _data.Permissions.Contains(permission, StringComparer.Ordinal);
    }

    public bool HasAnyPermission(params string[] permissions)
    {
        if (_data is null) throw new InvalidOperationException("CurrentUser.LoadAsync must be called first.");
        return permissions.Any(_data.Permissions.Contains);
    }

    public bool HasRole(string roleCode)
    {
        if (_data is null) throw new InvalidOperationException("CurrentUser.LoadAsync must be called first.");
        return _data.Roles.Any(x => x.Code == roleCode);
    }

    public AuditContext GetAuditContext()
    {
        var context = HttpContextAccessor.HttpContext;
        return new AuditContext(Id, string.Join(",", _data?.Roles.Select(x => x.Code) ?? []),
            context?.TraceIdentifier, context?.Connection.RemoteIpAddress?.ToString(), context?.Request.Headers.UserAgent.ToString());
    }
}
