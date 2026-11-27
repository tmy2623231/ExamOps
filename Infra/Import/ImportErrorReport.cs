using System.Text;
using ExamOps.DAL.Repositories;
using ExamOps.Model.Dtos;

namespace ExamOps.Infra.Import;

public static class ImportErrorReport
{
    public static byte[] CreateCsv(QuestionImportBatchDetail detail)
    {
        var builder = new StringBuilder("\uFEFF");
        builder.AppendLine("行号,题目编码,状态,题干,错误,警告");
        foreach (var row in detail.Rows.Where(x => !string.IsNullOrWhiteSpace(x.Errors) || !string.IsNullOrWhiteSpace(x.Warnings)))
            builder.AppendLine(string.Join(',', new[]
            {
                Cell(row.RowNo), Cell(row.ExternalCode), Cell(row.Status), Cell(row.Stem), Cell(row.Errors), Cell(row.Warnings)
            }));
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static string Cell(object? value)
    {
        var text = value?.ToString() ?? "";
        // 导出文本避免电子表格把用户内容当公式执行。
        if (text.StartsWith('=') || text.StartsWith('+') || text.StartsWith('-') || text.StartsWith('@')
            || text.StartsWith('\t') || text.StartsWith('\r')) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
