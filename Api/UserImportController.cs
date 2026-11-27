using ExamOps.BLL;
using ExamOps.Infra.Import;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ExamOps.Api;

[ApiController]
[Route("api/user-imports")]
[Authorize(Policy = Permissions.UserCreate)]
public sealed class UserImportController : ControllerBase
{
    [HttpGet("template")]
    public IActionResult DownloadTemplate()
    {
        var bytes = UserExcelImportHelper.BuildTemplateXlsx();
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.CacheControl = "no-store";
        return File(bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "ExamOps-员工导入模板.xlsx");
    }
}
