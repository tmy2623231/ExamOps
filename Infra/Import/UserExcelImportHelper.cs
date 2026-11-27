using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ExamOps.Model.Dtos;

namespace ExamOps.Infra.Import;

/// <summary>
/// Dependency-free OOXML (.xlsx) template generator and safe parser for batch user import.
/// Enforces ZIP magic header, entry cap, size cap, and rejects formula cells.
/// </summary>
public static class UserExcelImportHelper
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private const int MaxRows = 1000;
    private static readonly XNamespace MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    public static byte[] BuildTemplateXlsx()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "[Content_Types].xml",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>""");
            WriteEntry(zip, "_rels/.rels",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            WriteEntry(zip, "xl/workbook.xml",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="员工导入" sheetId="1" r:id="rId1"/></sheets></workbook>""");
            WriteEntry(zip, "xl/_rels/workbook.xml.rels",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>""");

            string[][] rows =
            [
                ["账号(必填)", "姓名(必填)", "部门名称(必填)", "岗位角色(考生/部门考务/命题老师/阅卷复核员)", "邮箱(可选)", "初始密码(留空自动生成12位)"],
                ["zhangsan", "张三", "示例组织", "考生", "zhangsan@example.com", ""],
                ["lisi", "李四", "示例组织", "部门考务", "", ""]
            ];

            var sb = new StringBuilder();
            sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
            for (var r = 0; r < rows.Length; r++)
            {
                sb.Append($"<row r=\"{r + 1}\">");
                for (var c = 0; c < rows[r].Length; c++)
                {
                    var col = (char)('A' + c);
                    var escaped = System.Security.SecurityElement.Escape(rows[r][c]) ?? "";
                    sb.Append($"<c r=\"{col}{r + 1}\" t=\"inlineStr\"><is><t>{escaped}</t></is></c>");
                }
                sb.Append("</row>");
            }
            sb.Append("</sheetData></worksheet>");
            WriteEntry(zip, "xl/worksheets/sheet1.xml", sb.ToString());
        }
        return ms.ToArray();
    }

    public static async Task<(IReadOnlyList<UserImportRowInput> Rows, string? Error)> ParseAsync(Stream input, string fileName)
    {
        if (!fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            return ([], "仅支持 .xlsx 格式的员工导入模板。");

        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer)) > 0)
        {
            total += read;
            if (total > MaxBytes) return ([], "文件大小不能超过 5 MB。");
            ms.Write(buffer, 0, read);
        }
        if (total < 4) return ([], "上传文件为空或已损坏。");
        var bytes = ms.ToArray();
        if (bytes[0] != 0x50 || bytes[1] != 0x4B || bytes[2] != 0x03 || bytes[3] != 0x04)
            return ([], "文件内容不是合法的 .xlsx (ZIP) 文件。");

        ms.Position = 0;
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);
        if (zip.Entries.Count > 128)
            return ([], "压缩包条目数量超出安全限制。");

        var sharedStrings = ReadSharedStrings(zip);
        var sheetEntry = zip.Entries.FirstOrDefault(e =>
            e.FullName.Equals("xl/worksheets/sheet1.xml", StringComparison.OrdinalIgnoreCase))
            ?? zip.Entries.FirstOrDefault(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
        if (sheetEntry is null)
            return ([], "未在工作簿中找到有效工作表。");

        using var sheetStream = sheetEntry.Open();
        var doc = XDocument.Load(sheetStream);
        var rowEls = doc.Descendants(MainNs + "row").ToList();
        if (rowEls.Count <= 1)
            return ([], "表格中没有可导入的数据行（第 1 行为表头）。");

        var result = new List<UserImportRowInput>();
        foreach (var rowEl in rowEls.Skip(1))
        {
            var rowNo = int.TryParse(rowEl.Attribute("r")?.Value, out var rn) ? rn : result.Count + 2;
            var cells = new string[6];
            foreach (var cell in rowEl.Elements(MainNs + "c"))
            {
                if (cell.Element(MainNs + "f") is not null)
                    return ([], $"第 {rowNo} 行包含公式，出于安全原因拒绝导入。");
                var colIdx = ParseColumnIndex(cell.Attribute("r")?.Value);
                if (colIdx is < 0 or >= 6) continue;
                cells[colIdx] = ReadCellText(cell, sharedStrings).Trim();
            }

            if (cells.All(string.IsNullOrWhiteSpace)) continue;
            if (result.Count >= MaxRows)
                return ([], $"单次最多导入 {MaxRows} 名员工。");

            result.Add(new UserImportRowInput(
                rowNo,
                cells[0],
                cells[1],
                cells[2],
                string.IsNullOrWhiteSpace(cells[3]) ? "考生" : cells[3],
                string.IsNullOrWhiteSpace(cells[4]) ? null : cells[4],
                string.IsNullOrWhiteSpace(cells[5]) ? null : cells[5]));
        }

        return result.Count == 0 ? ([], "未读取到有效员工数据行。") : (result, null);
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var list = new List<string>();
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return list;
        using var s = entry.Open();
        var doc = XDocument.Load(s);
        foreach (var si in doc.Descendants(MainNs + "si"))
            list.Add(string.Concat(si.Descendants(MainNs + "t").Select(t => t.Value)));
        return list;
    }

    private static string ReadCellText(XElement cell, List<string> sharedStrings)
    {
        var type = cell.Attribute("t")?.Value;
        if (type == "inlineStr")
            return string.Concat(cell.Descendants(MainNs + "t").Select(t => t.Value));
        var raw = cell.Element(MainNs + "v")?.Value ?? "";
        if (type == "s" && int.TryParse(raw, out var idx) && idx >= 0 && idx < sharedStrings.Count)
            return sharedStrings[idx];
        return raw;
    }

    private static int ParseColumnIndex(string? cellRef)
    {
        if (string.IsNullOrWhiteSpace(cellRef)) return -1;
        var col = 0;
        foreach (var ch in cellRef)
        {
            if (ch is >= 'A' and <= 'Z') col = col * 26 + (ch - 'A' + 1);
            else if (ch is >= 'a' and <= 'z') col = col * 26 + (ch - 'a' + 1);
            else break;
        }
        return col - 1;
    }

    private static void WriteEntry(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
