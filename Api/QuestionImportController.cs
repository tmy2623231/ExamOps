using System.Diagnostics;
using System.Text.Json.Serialization;
using ExamOps.BLL;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ExamOps.Api;

[ApiController]
[Route("api")]
[Authorize]
[RequestSizeLimit(12 * 1024 * 1024)]
public sealed class QuestionImportController(
    QuestionImportService imports,
    ICurrentUser currentUser,
    IWebHostEnvironment environment) : ControllerBase
{
    [HttpGet("question-banks/template")]
    public async Task<IActionResult> Template(CancellationToken cancellationToken)
    {
        await currentUser.LoadAsync();
        if (!currentUser.IsAuthenticated
            || (!currentUser.HasPermission(Permissions.QuestionImport) && !currentUser.HasPermission(Permissions.QuestionRead)))
        {
            return StatusCode(StatusCodes.Status403Forbidden, ApiEnvelope<object>.Fail("权限不足", "PERMISSION_DENIED"));
        }
        var path = Path.Combine(environment.WebRootPath, "files", "question-import-template.xlsx");
        if (!System.IO.File.Exists(path)) return NotFound();
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return PhysicalFile(path, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "题目导入模板.xlsx");
    }

    [HttpPost("question-banks/{bankId:long}/imports/preview")]
    [EnableRateLimiting("import")]
    public async Task<IActionResult> Preview(long bankId, IFormFile file, [FromForm] string mode, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0) return BadRequest(ApiEnvelope<object>.Fail("请选择要上传的 .xlsx 文件。", "FILE_REQUIRED"));
        await using var stream = file.OpenReadStream();
        var result = await imports.PreviewAsync(bankId, stream, file.FileName, mode, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("question-banks/{bankId:long}/imports")]
    [EnableRateLimiting("import")]
    public async Task<IActionResult> Confirm(long bankId, [FromBody] ImportConfirmInput input, CancellationToken cancellationToken)
    {
        var result = await imports.ConfirmAsync(bankId, input, cancellationToken);
        return ToActionResult(result);
    }

    [HttpGet("imports/{batchId:long}")]
    public async Task<IActionResult> Detail(long batchId, [FromQuery] int pageIndex = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var result = await imports.GetDetailAsync(batchId, pageIndex, pageSize, cancellationToken);
        return ToActionResult(result);
    }

    [HttpGet("imports/{batchId:long}/rows")]
    public async Task<IActionResult> Rows(long batchId, [FromQuery] int pageIndex = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var result = await imports.GetDetailAsync(batchId, pageIndex, pageSize, cancellationToken);
        if (!result.IsSuccess) return ToActionResult(result);
        return Ok(ApiEnvelope<object>.Ok(new { items = result.Value!.Rows, totalCount = result.Value.TotalCount }));
    }

    [HttpGet("imports/{batchId:long}/errors")]
    public async Task<IActionResult> Errors(long batchId, CancellationToken cancellationToken = default)
    {
        var result = await imports.GetErrorsAsync(batchId, cancellationToken);
        return ToActionResult(result);
    }

    [HttpGet("imports/{batchId:long}/error-report")]
    public async Task<IActionResult> ErrorReport(long batchId, CancellationToken cancellationToken = default)
    {
        var result = await imports.GetErrorReportAsync(batchId, cancellationToken);
        if (!result.IsSuccess) return ToActionResult(result);
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Cache-Control"] = "no-store";
        return File(result.Value!, "text/csv; charset=utf-8", $"题目导入错误-{batchId}.csv");
    }

    private IActionResult ToActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess) return Ok(ApiEnvelope<T>.Ok(result.Value!));
        var envelope = ApiEnvelope<T>.Fail(result.Error ?? "操作失败。", result.Code);
        return result.Code switch
        {
            "PERMISSION_DENIED" => StatusCode(StatusCodes.Status403Forbidden, envelope),
            "NOT_FOUND" or "OUT_OF_SCOPE" => NotFound(envelope),
            "CONCURRENCY_CONFLICT" or "DUPLICATE_EXTERNAL_CODE" or "IMPORT_CONFLICT" or "INVALID_STATE" => Conflict(envelope),
            _ => BadRequest(envelope)
        };
    }

    private sealed record ApiEnvelope<T>([property: JsonPropertyName("ok")] bool Success, T? Data, string? Error, string? Code, string TraceId)
    {
        public static ApiEnvelope<T> Ok(T data) => new(true, data, null, null, Activity.Current?.Id ?? "");
        public static ApiEnvelope<T> Fail(string error, string? code) => new(false, default, error, code, Activity.Current?.Id ?? "");
    }
}
