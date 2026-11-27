using ExamOps.Model.Entities;
using Microsoft.EntityFrameworkCore;

namespace ExamOps.DAL;

public sealed class ExamDbContext(DbContextOptions<ExamDbContext> options) : DbContext(options)
{
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserDepartment> UserDepartments => Set<UserDepartment>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<QuestionBank> QuestionBanks => Set<QuestionBank>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<QuestionVersion> QuestionVersions => Set<QuestionVersion>();
    public DbSet<QuestionOption> QuestionOptions => Set<QuestionOption>();
    public DbSet<QuestionTag> QuestionTags => Set<QuestionTag>();
    public DbSet<QuestionTagLink> QuestionTagLinks => Set<QuestionTagLink>();
    public DbSet<QuestionCategory> QuestionCategories => Set<QuestionCategory>();
    public DbSet<QuestionCategoryLink> QuestionCategoryLinks => Set<QuestionCategoryLink>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<ImportRow> ImportRows => Set<ImportRow>();
    public DbSet<Paper> Papers => Set<Paper>();
    public DbSet<PaperVersion> PaperVersions => Set<PaperVersion>();
    public DbSet<PaperSection> PaperSections => Set<PaperSection>();
    public DbSet<PaperQuestion> PaperQuestions => Set<PaperQuestion>();
    public DbSet<ExamTask> ExamTasks => Set<ExamTask>();
    public DbSet<ExamTaskStaff> ExamTaskStaffs => Set<ExamTaskStaff>();
    public DbSet<AssignmentBatch> AssignmentBatches => Set<AssignmentBatch>();
    public DbSet<AssignmentBatchRow> AssignmentBatchRows => Set<AssignmentBatchRow>();
    public DbSet<ExamAssignment> ExamAssignments => Set<ExamAssignment>();
    public DbSet<RetakeGrant> RetakeGrants => Set<RetakeGrant>();
    public DbSet<RevocationRecord> RevocationRecords => Set<RevocationRecord>();
    public DbSet<ExamAttempt> ExamAttempts => Set<ExamAttempt>();
    public DbSet<AttemptAnswer> AttemptAnswers => Set<AttemptAnswer>();
    public DbSet<AttemptEvent> AttemptEvents => Set<AttemptEvent>();
    public DbSet<ScoreAdjustment> ScoreAdjustments => Set<ScoreAdjustment>();
    public DbSet<ResultRelease> ResultReleases => Set<ResultRelease>();
    public DbSet<ResultReleaseItem> ResultReleaseItems => Set<ResultReleaseItem>();
    public DbSet<ReportExport> ReportExports => Set<ReportExport>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyConfigurationsFromAssembly(typeof(ExamDbContext).Assembly);
}
