using System.Globalization;
using System.Text.Json;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;

namespace ExamOps.Components.Shared;

public static class ExamLabels
{
    public static string TaskStatus(string status) => status switch
    {
        "Draft" => "草稿", "Published" => "已发布", "Closed" => "已关闭",
        "Revoked" => "已撤回", "Archived" => "已归档", _ => status
    };
    public static string AssignmentStatus(string status) => status switch
    {
        "Pending" => "待生效", "Available" => "可考试", "InProgress" => "考试中",
        "Submitted" => "已交卷", "Graded" => "已阅卷", "Closed" => "已结束",
        "Expired" => "已过期", "Revoked" => "已撤回", _ => status
    };
    public static string AttemptStatus(string? status) => status switch
    {
        "InProgress" => "作答中", "Submitted" => "已交卷", "AutoSubmitted" => "超时自动交卷",
        "Graded" => "已阅卷", "Voided" => "已作废", null or "" => "未开始", _ => status
    };
    public static string BatchStatus(string status) => status switch
    {
        "Pending" => "待处理", "Running" => "处理中", "Completed" => "已完成", "Success" => "成功",
        "PartiallyFailed" => "部分失败", "Failed" => "失败", "Revoked" => "已撤回", _ => status
    };
    public static string PreviewOutcome(string outcome) => outcome switch
    {
        "Eligible" => "可下发", "Skipped" => "将跳过", "Failed" => "不可下发", _ => outcome
    };
    public static string BadgeClass(string status) => status switch
    {
        "Draft" or "Pending" => "badge-warning", "Published" or "Available" or "Completed" or "Success" or "Graded" => "badge-success",
        "InProgress" or "Running" or "Submitted" or "AutoSubmitted" => "badge-info",
        "Closed" or "Expired" or "Archived" or "Revoked" or "Voided" or "Skipped" => "badge-neutral",
        "Failed" or "PartiallyFailed" => "badge-danger", _ => "badge-neutral"
    };
    public static string ScorePolicy(string value) => value switch
    {
        "Latest" => "取最后一次", "Highest" => "取最高分", "First" => "取第一次", "Average" => "取平均分", _ => value
    };
    public static string RetakeApproval(string value) => value == "Manager" ? "管理员批准" : "无需审批";
    public static string ResultPolicy(string value) => value switch
    {
        "Immediate" => "交卷后立即公布", "AfterClose" => "考试关闭后公布", "Manual" => "管理员手动公布", _ => value
    };
    public static string ResultStatus(string value) => value switch
    {
        "Unpublished" => "未公布", "Published" => "已公布", "Withdrawn" => "已撤回", _ => value
    };
    public static string ReviewType(string value) => value switch
    {
        ReviewTypes.FormatAnomaly => "格式异常", ReviewTypes.Dispute => "判分异议",
        ReviewTypes.VoidRequest => "作废申请", _ => value
    };
    public static string LatePolicy(string value) => value == "Zero" ? "超时答卷计 0 分" : "超时答卷正常计分";
    public static string StaffRole(string value) => value switch
    {
        "ExamManager" => "考务管理员", "Grader" => "阅卷人", "Invigilator" => "监考员", _ => value
    };
    public static string RevocationPolicy(string value) => value switch
    {
        "KeepScore" => "保留已保存答案与成绩", "VoidScore" => "保留答卷，但成绩计 0 分",
        "ForceSubmit" => "立即按当前答案交卷并阅卷", "VoidAttempt" => "作废进行中的答卷", _ => value
    };
    public static string QuestionType(string value) => value switch
    {
        "SingleChoice" => "单选题", "MultipleChoice" => "多选题", "TrueFalse" => "判断题", _ => value
    };
    public static string InputTime(DateTime value) => TimeFormat.ToLocal(value).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
    public static string Countdown(TimeSpan remaining) => remaining <= TimeSpan.Zero
        ? "00:00"
        : remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}"
            : $"{remaining.Minutes:00}:{remaining.Seconds:00}";
    public static bool CanStart(MyExamRow row, DateTime localNow) => row.Status == "Available"
        && row.CurrentAttemptStatus != "InProgress"
        && row.AvailableFrom <= localNow && localNow < row.AvailableTo
        && row.AttemptsUsed + row.InheritedAttemptsUsed < row.AttemptLimit + row.GrantedAttempts;
    public static string BatchRowMessage(string? errorJson)
    {
        if (string.IsNullOrWhiteSpace(errorJson) || errorJson == "[]") return "—";
        try
        {
            using var document = JsonDocument.Parse(errorJson);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().Select(item => item.TryGetProperty("message", out var message) ? message.GetString() : null)
                    .FirstOrDefault(message => !string.IsNullOrWhiteSpace(message)) ?? "处理结果不可用，请联系管理员。"
                : "处理结果不可用，请联系管理员。";
        }
        catch (JsonException) { return "处理结果不可用，请联系管理员。"; }
    }
}
