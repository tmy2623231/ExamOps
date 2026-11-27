using ExamOps.DAL.Repositories;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;

namespace ExamOps.BLL;

public interface ICurrentRequestContext
{
    long? UserId { get; }
    string? DisplayName { get; }
    string? Username { get; }
    string? TraceId { get; }
    string? Ip { get; }
    string? UserAgent { get; }
}

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

    public CurrentUserService(AccessRepository repository, ICurrentRequestContext request)
        : this(userId => repository.GetAsync(userId), request)
    {
    }

    internal CurrentUserService(Func<long, Task<CurrentUserData?>> getData, ICurrentRequestContext request)
    {
        _getData = getData;
        _request = request;
    }

    private readonly ICurrentRequestContext _request;

    public long? Id => _request.UserId;
    public string? DisplayName => _data?.DisplayName ?? _request.DisplayName;
    public string? Username => _data?.Username ?? _request.Username;
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
        return new AuditContext(Id, string.Join(",", _data?.Roles.Select(x => x.Code) ?? []),
            _request.TraceId, _request.Ip, _request.UserAgent);
    }
}
