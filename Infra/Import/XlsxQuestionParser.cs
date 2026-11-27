using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace ExamOps.Infra.Import;

public static class XlsxQuestionParser
{
    private static readonly XNamespace SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace OfficeRelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationshipNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const int FirstDataRow = 2;
    private const int LastDataRow = 5001;
    private const int MaxDataRows = 5000;
    private const int TemplateColumnCount = 14;

    public static QuestionImportParseResult Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var validation = ImportFileValidator.Prepare(stream, out var package);
        if (package is null) return Fail(validation.Code, validation.Error);
        using (package)
        {
            try
            {
                return ParsePackage(package);
            }
            catch (XlsxParseException ex)
            {
                return Fail(ex.Code, ex.Message);
            }
            catch (XmlException)
            {
                return Fail("INVALID_XML", "XLSX中包含不安全或无效的XML。");
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
            {
                return Fail("INVALID_ZIP", "文件不是有效的ZIP/OOXML文件。");
            }
        }
    }

    private static QuestionImportParseResult ParsePackage(Stream package)
    {
        using var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        ValidateWorkbook(archive);
        var sharedStrings = LoadSharedStrings(archive);
        var worksheet = archive.GetEntry("xl/worksheets/sheet1.xml")
            ?? throw new XlsxParseException("MISSING_WORKSHEET", "XLSX缺少xl/worksheets/sheet1.xml。");
        if (!ImportFileValidator.TryReadEntry(worksheet, ImportFileValidator.MaxExpandedWorksheetXmlBytes, out var xml)
            || xml is null)
        {
            throw new XlsxParseException("WORKSHEET_TOO_LARGE", "工作表XML展开后不能超过20MiB。");
        }

        using (xml)
        {
            using var reader = XmlReader.Create(xml, ImportFileValidator.CreateXmlReaderSettings());
            return ParseWorksheet(reader, sharedStrings);
        }
    }

    private static void ValidateWorkbook(ZipArchive archive)
    {
        var workbookEntry = archive.GetEntry("xl/workbook.xml")
            ?? throw new XlsxParseException("MISSING_WORKBOOK", "XLSX缺少xl/workbook.xml。");
        if (!ImportFileValidator.TryReadEntry(workbookEntry, ImportFileValidator.MaxExpandedWorksheetXmlBytes, out var workbookXml)
            || workbookXml is null)
        {
            throw new XlsxParseException("XML_TOO_LARGE", "XLSX的XML部件超过大小限制。");
        }

        XDocument workbook;
        using (workbookXml)
        {
            using var reader = XmlReader.Create(workbookXml, ImportFileValidator.CreateXmlReaderSettings());
            workbook = XDocument.Load(reader);
        }

        var sheet = workbook.Root?
            .Element(SpreadsheetNamespace + "sheets")?
            .Elements(SpreadsheetNamespace + "sheet")
            .FirstOrDefault();
        if (sheet is null || (sheet.Attribute("name")?.Value != "题目"))
        {
            throw new XlsxParseException("INVALID_SHEET", "第一个工作表必须名为“题目”。");
        }

        var relationshipId = sheet.Attribute(OfficeRelationshipNamespace + "id")?.Value;
        var relationshipsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels")
            ?? throw new XlsxParseException("INVALID_WORKBOOK", "XLSX缺少工作表关系定义。");
        if (!ImportFileValidator.TryReadEntry(relationshipsEntry, ImportFileValidator.MaxExpandedWorksheetXmlBytes, out var relationshipsXml)
            || relationshipsXml is null)
        {
            throw new XlsxParseException("XML_TOO_LARGE", "XLSX的XML部件超过大小限制。");
        }

        XDocument relationships;
        using (relationshipsXml)
        {
            using var reader = XmlReader.Create(relationshipsXml, ImportFileValidator.CreateXmlReaderSettings());
            relationships = XDocument.Load(reader);
        }

        var relationship = relationships.Root?
            .Elements(PackageRelationshipNamespace + "Relationship")
            .FirstOrDefault(element => element.Attribute("Id")?.Value == relationshipId);
        var type = relationship?.Attribute("Type")?.Value ?? "";
        var target = relationship?.Attribute("Target")?.Value ?? "";
        if (relationship is null
            || !type.EndsWith("/worksheet", StringComparison.OrdinalIgnoreCase)
            || string.Equals(relationship.Attribute("TargetMode")?.Value, "External", StringComparison.OrdinalIgnoreCase)
            || (target != "worksheets/sheet1.xml" && target != "/xl/worksheets/sheet1.xml"))
        {
            throw new XlsxParseException("INVALID_SHEET", "“题目”工作表必须对应xl/worksheets/sheet1.xml。");
        }
    }

    private static IReadOnlyList<string> LoadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return [];
        if (!ImportFileValidator.TryReadEntry(entry, ImportFileValidator.MaxExpandedWorksheetXmlBytes, out var xml)
            || xml is null)
        {
            throw new XlsxParseException("XML_TOO_LARGE", "共享字符串XML展开后不能超过20MiB。");
        }

        using (xml)
        {
            using var reader = XmlReader.Create(xml, ImportFileValidator.CreateXmlReaderSettings());
            var document = XDocument.Load(reader);
            if (document.Root?.Name != SpreadsheetNamespace + "sst")
            {
                throw new XlsxParseException("INVALID_XML", "共享字符串XML结构无效。");
            }

            return document.Root.Elements(SpreadsheetNamespace + "si")
                .Select(ExtractStringText)
                .ToArray();
        }
    }

    private static QuestionImportParseResult ParseWorksheet(XmlReader reader, IReadOnlyList<string> sharedStrings)
    {
        var rows = new List<QuestionImportParsedRow>();
        string[]? header = null;
        var lastRowNo = 0;
        var inferredRowNo = 1;
        var dataRowCount = 0;

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row") continue;
            var rowNo = ReadRowNumber(reader, inferredRowNo, lastRowNo);
            inferredRowNo = rowNo == int.MaxValue ? throw new XlsxParseException("INVALID_ROW", "XLSX行号无效。") : rowNo + 1;
            lastRowNo = rowNo;
            var cells = Enumerable.Repeat("", TemplateColumnCount).ToArray();
            var nextColumn = 1;
            var hasUnexpectedValue = false;

            if (!reader.IsEmptyElement)
            {
                reader.Read();
                while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "row"))
                {
                    if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "c")
                    {
                        var cell = ReadCell(reader, rowNo, nextColumn, sharedStrings);
                        nextColumn = cell.Column + 1;
                        if (cell.Column >= 1 && cell.Column <= TemplateColumnCount)
                        {
                            cells[cell.Column - 1] = cell.Text;
                        }
                        else if (cell.Text.Trim().Length > 0)
                        {
                            hasUnexpectedValue = true;
                        }
                    }
                    else
                    {
                        reader.Read();
                    }
                }

                if (reader.EOF) throw new XlsxParseException("INVALID_XML", "工作表XML结构不完整。");
            }

            if (header is null)
            {
                if (rowNo != 1 || hasUnexpectedValue || !HeaderMatches(cells))
                {
                    throw new XlsxParseException("HEADER_MISMATCH", "第1行必须严格包含模板的14个列名。");
                }

                header = cells;
                continue;
            }

            if (cells.All(value => value.Trim().Length == 0))
            {
                if (hasUnexpectedValue)
                {
                    throw new XlsxParseException("UNEXPECTED_COLUMN", "数据行不能包含模板N列之后的内容。");
                }

                continue;
            }

            if (hasUnexpectedValue)
            {
                throw new XlsxParseException("UNEXPECTED_COLUMN", "数据行不能包含模板N列之后的内容。");
            }

            dataRowCount++;
            if (dataRowCount > MaxDataRows)
            {
                throw new XlsxParseException("TOO_MANY_ROWS", "数据行不能超过5000行。");
            }

            if (rowNo < FirstDataRow || rowNo > LastDataRow)
            {
                throw new XlsxParseException("ROW_OUT_OF_RANGE", "数据行必须位于第2至5001行。");
            }

            rows.Add(XlsxImportRules.ValidateRow(rowNo, cells.Select(value => value.Trim()).ToArray()));
        }

        if (header is null)
        {
            throw new XlsxParseException("HEADER_MISMATCH", "第1行必须严格包含模板的14个列名。");
        }

        var duplicateCodes = rows
            .Where(row => row.ExternalCode is not null)
            .GroupBy(row => row.ExternalCode!, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var parsedRows = rows
            .Select(row => duplicateCodes.Contains(row.ExternalCode!)
                ? row with
                {
                    Errors = row.Errors.Append($"题目编码“{row.ExternalCode}”在文件中重复。").ToArray()
                }
                : row)
            .ToArray();
        return new QuestionImportParseResult(true, null, null, parsedRows);
    }

    private static bool HeaderMatches(IReadOnlyList<string> cells)
        => XlsxImportRules.HeaderNames.Select((name, index) => cells[index].Trim() == name).All(match => match);

    private static int ReadRowNumber(XmlReader reader, int inferredRowNo, int lastRowNo)
    {
        var value = reader.GetAttribute("r");
        int rowNo;
        if (value is null)
        {
            rowNo = inferredRowNo;
        }
        else if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out rowNo) || rowNo < 1)
        {
            throw new XlsxParseException("INVALID_ROW", "XLSX行号无效。");
        }

        if (rowNo <= lastRowNo) throw new XlsxParseException("INVALID_ROW", "XLSX行号必须严格递增。");
        return rowNo;
    }

    private static ParsedCell ReadCell(
        XmlReader reader,
        int rowNo,
        int inferredColumn,
        IReadOnlyList<string> sharedStrings)
    {
        var reference = reader.GetAttribute("r");
        var column = inferredColumn;
        if (reference is not null)
        {
            var address = ParseCellAddress(reference);
            if (address.Row != rowNo)
            {
                throw new XlsxParseException("INVALID_CELL", "单元格引用行号与所在行不一致。");
            }

            column = address.Column;
        }

        if (column == int.MaxValue)
        {
            throw new XlsxParseException("INVALID_CELL", "单元格引用列号无效。");
        }

        var cellType = reader.GetAttribute("t");
        var value = "";
        if (reader.IsEmptyElement)
        {
            reader.Read();
        }
        else
        {
            reader.Read();
            while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "c"))
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "f")
                {
                    throw new XlsxParseException("FORMULAS_NOT_ALLOWED", "XLSX不能包含公式单元格。");
                }

                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "v")
                {
                    value = reader.ReadElementContentAsString();
                }
                else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "is")
                {
                    value = ReadInlineString(reader);
                }
                else
                {
                    reader.Read();
                }
            }

            if (reader.EOF) throw new XlsxParseException("INVALID_XML", "工作表XML结构不完整。");
            reader.Read();
        }

        return new ParsedCell(column, ResolveCellText(value, cellType, sharedStrings));
    }

    private static string ReadInlineString(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return "";
        }

        using var subtree = reader.ReadSubtree();
        var element = XElement.Load(subtree);
        reader.Read();
        return ExtractStringText(element);
    }

    private static string ResolveCellText(string value, string? cellType, IReadOnlyList<string> sharedStrings)
    {
        if (cellType == "s")
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                || index < 0
                || index >= sharedStrings.Count)
            {
                throw new XlsxParseException("INVALID_SHARED_STRING", "单元格引用了不存在的共享字符串。");
            }

            return sharedStrings[index];
        }

        if (cellType == "b")
        {
            return value switch
            {
                "1" => "true",
                "0" => "false",
                _ => value
            };
        }

        return value;
    }

    private static CellAddress ParseCellAddress(string reference)
    {
        var digitIndex = 0;
        while (digitIndex < reference.Length && reference[digitIndex] is >= 'A' and <= 'Z') digitIndex++;
        if (digitIndex == 0 || digitIndex == reference.Length)
        {
            throw new XlsxParseException("INVALID_CELL", "单元格引用格式无效。");
        }

        var column = 0;
        foreach (var character in reference.AsSpan(0, digitIndex))
        {
            if (column > (int.MaxValue - (character - 'A' + 1)) / 26)
            {
                throw new XlsxParseException("INVALID_CELL", "单元格引用列号无效。");
            }

            column = column * 26 + character - 'A' + 1;
        }

        if (!int.TryParse(reference.AsSpan(digitIndex), NumberStyles.None, CultureInfo.InvariantCulture, out var row) || row < 1)
        {
            throw new XlsxParseException("INVALID_CELL", "单元格引用格式无效。");
        }

        return new CellAddress(column, row);
    }

    private static string ExtractStringText(XElement container)
    {
        var text = new System.Text.StringBuilder();
        foreach (var child in container.Elements())
        {
            if (child.Name == SpreadsheetNamespace + "t")
            {
                text.Append(child.Value);
            }
            else if (child.Name == SpreadsheetNamespace + "r")
            {
                text.Append(child.Element(SpreadsheetNamespace + "t")?.Value);
            }
        }

        return text.ToString();
    }

    private static QuestionImportParseResult Fail(string code, string? error)
        => new(false, code, error, []);

    private sealed record ParsedCell(int Column, string Text);
    private sealed record CellAddress(int Column, int Row);

    private sealed class XlsxParseException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}

public sealed record QuestionImportParseResult(
    bool IsSuccess,
    string? ErrorCode,
    string? Error,
    IReadOnlyList<QuestionImportParsedRow> Rows);
