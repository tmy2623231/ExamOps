using ExamOps.DAL;
using ExamOps.Model.Dtos;
using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace ExamOps.DAL.Repositories;

public sealed class DepartmentRepository(IUnitOfWork uow)
{
    private ExamDbContext Db => uow.Db;

    public async Task<IReadOnlyList<DepartmentRow>> GetAllAsync()
    {
        var db = Db;
        return await db.Departments.AsNoTracking()
            .OrderBy(x => x.Path)
            .Select(x => new DepartmentRow(
                x.Id, x.ParentId, x.Name, x.Path, x.SortOrder, x.IsActive,
                db.Departments.Count(d => d.ParentId == x.Id),
                db.Users.Count(u => u.PrimaryDepartmentId == x.Id),
                x.RowVersion))
            .ToListAsync();
    }

    public async Task<Department?> GetByIdAsync(long id)
    {
        var db = Db;
        return await db.Departments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
    }

    public async Task<Department?> GetByPathAsync(string path)
    {
        var db = Db;
        return await db.Departments.AsNoTracking().FirstOrDefaultAsync(x => x.Path == path);
    }

    public async Task<bool> ExistsChildAsync(long parentId)
    {
        var db = Db;
        return await db.Departments.AsNoTracking().AnyAsync(x => x.ParentId == parentId && x.IsActive);
    }

    public async Task<int> CountPrimaryUsersAsync(long departmentId)
    {
        var db = Db;
        return await db.Users.AsNoTracking().CountAsync(x => x.PrimaryDepartmentId == departmentId && x.Status == "Active");
    }

    public Task<Department?> GetTrackedAsync(ExamDbContext db, long id)
        => db.Departments.FirstOrDefaultAsync(x => x.Id == id);

    public Task AddAsync(ExamDbContext db, Department department)
    {
        db.Departments.Add(department);
        return Task.CompletedTask;
    }

    public async Task<int> GetNextSortOrderAsync(ExamDbContext db, long? parentId)
        => await db.Departments.AsNoTracking().CountAsync(x => x.ParentId == parentId) + 1;
}
