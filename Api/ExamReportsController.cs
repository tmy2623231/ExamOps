using ExamOps.BLL;
using ExamOps.DAL;
using ExamOps.DAL.Repositories;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExamOps.Api;

[ApiController]
[Route("api")]
[Authorize]
public sealed class ExamReportsController(
    ReportService reports,
    IUnitOfWork uow,
    AuditRepository audit,
    ICurrentUser currentUser) : ExamApiControllerBase(uow, audit, currentUser)
{
    [HttpGet("reports/tasks/{taskId:long}/summary")]
    public Task<IActionResult> Summary(long taskId) => Table(reports.GetSummaryAsync(taskId));

    [HttpGet("reports/tasks/{taskId:long}/distribution")]
    public Task<IActionResult> Distribution(long taskId) => Table(reports.GetDistributionAsync(taskId));

    [HttpGet("reports/tasks/{taskId:long}/pass-rate")]
    public Task<IActionResult> PassRate(long taskId) => Table(reports.GetPassRateAsync(taskId));

    [HttpGet("reports/departments/compare")]
    public Task<IActionResult> DepartmentCompare([FromQuery] long? taskId, [FromQuery] DateTime? from, [FromQuery] DateTime? to)
        => Table(reports.GetDepartmentCompareAsync(new DepartmentCompareQuery(taskId, from, to)));

    [HttpGet("reports/questions/{questionVersionId:long}")]
    public Task<IActionResult> QuestionAccuracy(long questionVersionId, [FromQuery] long? taskId)
        => Table(reports.GetQuestionAccuracyAsync(questionVersionId, taskId));

    [HttpGet("reports/tasks/{taskId:long}/questions")]
    public Task<IActionResult> TaskQuestions(long taskId) => Table(reports.GetTaskQuestionsAsync(taskId));

    [HttpGet("reports/tasks/{taskId:long}/anomalies")]
    public async Task<IActionResult> Anomalies(long taskId, [FromQuery] int pageIndex = 1, [FromQuery] int pageSize = 50)
    {
        var result = await reports.GetAnomaliesAsync(taskId, new ReportQuery(pageIndex, pageSize));
        return result.IsSuccess ? Paged(result.Value!) : ToActionResult(result);
    }

    [HttpPost("reports/tasks/{taskId:long}/export")]
    public async Task<IActionResult> Export(long taskId, [FromBody] ExportReportInput input)
    {
        var api = await MetadataAsync(Permissions.ReportExport, true, "/api/reports/tasks/{taskId}/export", input);
        if (api.Error is not null) return api.Error;
        if (await ReplayAsync<ExportReportOutput>(api.Value) is { } replay) return replay;
        return ToActionResult(await reports.ExportAsync(taskId, input, api.Value));
    }

    [HttpGet("reports/exports/{exportId:long}")]
    public async Task<IActionResult> Download(long exportId)
    {
        var result = await reports.DownloadAsync(exportId);
        if (!result.IsSuccess) return ToActionResult(result);
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(result.Value!.Content, result.Value.Entity.Format == "CSV" ? "text/csv" :
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", result.Value.Entity.FileName);
    }

    private async Task<IActionResult> Table<T>(Task<Result<T>> action) => ToActionResult(await action);
}
