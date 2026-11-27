using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ExamOps.Model.Common;
using ExamOps.Model.Dtos;

namespace ExamOps.Infra.Export;

public static class ReportFileWriter
{
    private const int MaxExcelCellLength = 32767;
    private static readonly XNamespace SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    public static byte[] WriteCsv(ReportTable table)
    {
        using var stream = new MemoryStream();
        stream.Write(new UTF8Encoding(true).GetPreamble());
        using (var writer = new StreamWriter(stream, new UTF8Encoding(true), leaveOpen: true))
        {
            writer.WriteLine(string.Join(",", table.Columns.Select(x => Csv(x.Label))));
            foreach (var row in table.Rows)
                writer.WriteLine(string.Join(",", table.Columns.Select(column => Csv(Display(row.Values.GetValueOrDefault(column.Key), column.Kind)))));
        }
        return stream.ToArray();
    }

    public static byte[] WriteXlsx(ReportTable table)
    {
        if (table.Columns.Count == 0 || table.Columns.Count > 16384)
            throw new InvalidDataException("报表列数超出 XLSX 限制。");
        if (table.Rows.Count > 1048575)
            throw new InvalidDataException("报表行数超出 XLSX 限制。");

        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                </Types>
                """);
            WriteEntry(archive, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            WriteEntry(archive, "xl/workbook.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="Report" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """);
            WriteEntry(archive, "xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                </Relationships>
                """);

            var data = new XElement(SpreadsheetNs + "sheetData");
            data.Add(CreateRow(1, table.Columns.Select(x => InlineCell(x.Label))));
            var rowNumber = 2;
            foreach (var row in table.Rows)
            {
                data.Add(CreateRow(rowNumber++, table.Columns.Select(column =>
                {
                    var value = row.Values.GetValueOrDefault(column.Key);
                    return column.Kind is ReportColumnKind.Integer or ReportColumnKind.Decimal or ReportColumnKind.Percent
                        && value is not null
                        ? NumericCell(Convert.ToDecimal(value, CultureInfo.InvariantCulture), column.Kind)
                        : InlineCell(Display(value, column.Kind));
                })));
            }

            var lastColumn = ColumnName(table.Columns.Count);
            var worksheet = new XElement(SpreadsheetNs + "worksheet",
                new XElement(SpreadsheetNs + "dimension", new XAttribute("ref", $"A1:{lastColumn}{Math.Max(1, table.Rows.Count + 1)}")),
                new XElement(SpreadsheetNs + "sheetViews",
                    new XElement(SpreadsheetNs + "sheetView", new XAttribute("workbookViewId", "0"),
                        new XElement(SpreadsheetNs + "pane", new XAttribute("ySplit", "1"),
                            new XAttribute("topLeftCell", "A2"), new XAttribute("activePane", "bottomLeft"), new XAttribute("state", "frozen")))),
                new XElement(SpreadsheetNs + "cols", table.Columns.Select((column, index) =>
                    new XElement(SpreadsheetNs + "col", new XAttribute("min", index + 1), new XAttribute("max", index + 1),
                        new XAttribute("width", Math.Clamp(column.Label.Length + 4, 12, 60)), new XAttribute("customWidth", "1")))),
                data,
                new XElement(SpreadsheetNs + "autoFilter", new XAttribute("ref", $"A1:{lastColumn}{Math.Max(1, table.Rows.Count + 1)}")));
            WriteEntry(archive, "xl/worksheets/sheet1.xml", new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), worksheet).ToString());
        }
        return stream.ToArray();
    }

    private static XElement CreateRow(int number, IEnumerable<XElement> cells)
        => new(SpreadsheetNs + "row", new XAttribute("r", number), cells.Select((cell, index) =>
        {
            cell.SetAttributeValue("r", $"{ColumnName(index + 1)}{number}");
            return cell;
        }));

    private static XElement InlineCell(string value)
    {
        if (value.Length > MaxExcelCellLength) throw new InvalidDataException("单元格内容超出 XLSX 限制。");
        return new XElement(SpreadsheetNs + "c", new XAttribute("t", "inlineStr"),
            new XElement(SpreadsheetNs + "is", new XElement(SpreadsheetNs + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), value)));
    }

    private static XElement NumericCell(decimal value, ReportColumnKind kind)
    {
        var number = kind == ReportColumnKind.Percent ? value * 100m : value;
        return new XElement(SpreadsheetNs + "c", new XElement(SpreadsheetNs + "v", number.ToString("0.################", CultureInfo.InvariantCulture)));
    }

    private static string Display(object? value, ReportColumnKind kind)
    {
        if (value is null) return "";
        if (value is DateTime date) return TimeFormat.ToLocal(date).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        if (kind == ReportColumnKind.Percent && value is decimal percent) return (percent * 100m).ToString("0.00", CultureInfo.InvariantCulture);
        if (value is decimal number) return number.ToString("0.################", CultureInfo.InvariantCulture);
        if (value is IFormattable formattable) return formattable.ToString(null, CultureInfo.InvariantCulture);
        return value.ToString() ?? "";
    }

    private static string Csv(string value)
    {
        var safe = value.TrimStart();
        if (safe.Length > 0 && safe[0] is '=' or '+' or '-' or '@') value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    private static string ColumnName(int index)
    {
        var name = "";
        while (index > 0)
        {
            index--;
            name = (char)('A' + index % 26) + name;
            index /= 26;
        }
        return name;
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        using var stream = archive.CreateEntry(path).Open();
        var bytes = new UTF8Encoding(false).GetBytes(content);
        stream.Write(bytes);
    }
}
