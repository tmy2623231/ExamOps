using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace ExamOps.Infra.Import;

public static class ImportParserCheck
{
    private static readonly XNamespace SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace OfficeRelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationshipNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace ContentTypeNamespace = "http://schemas.openxmlformats.org/package/2006/content-types";

    public static void Run()
    {
        var validRows = CreateRows();
        using var validWorkbook = CreateWorkbook(validRows);
        var parsed = XlsxQuestionParser.Parse(validWorkbook);
        Check(parsed.IsSuccess && parsed.Rows.Count == 3, $"three valid rows parse ({parsed.ErrorCode}: {parsed.Error})");
        Check(parsed.Rows.All(row => row.Errors.Count == 0), "valid rows have no errors");
        Check(parsed.Rows[0].Stem == "共享字符串题干" && parsed.Rows[0].Type == "SingleChoice", "shared strings and type mapping");
        Check(parsed.Rows[0].AnswerValues.SequenceEqual(["A"]) && parsed.Rows[0].DefaultScore == 5m, "single answer and score");
        Check(parsed.Rows[1].AnswerValues.SequenceEqual(["A", "C"]) && parsed.Rows[1].AnswerValue == "A,C", "multiple answers dedupe and sort");
        Check(parsed.Rows[1].TagNames.SequenceEqual(["代数", "数学"]), "tags dedupe");
        Check(parsed.Rows[2].Stem == "内联字符串题干", "inline strings");
        Check(!parsed.Rows[2].TrueFalseAnswer
            && parsed.Rows[2].Options.Select(option => (option.Label, option.Text)).SequenceEqual([("A", "正确"), ("B", "错误")])
            && parsed.Rows[2].Warnings.Count == 2, "true-false options, answer, and warnings");

        var invalidAnswerRows = CreateRows();
        invalidAnswerRows[0][9] = "G";
        using var invalidAnswerWorkbook = CreateWorkbook(invalidAnswerRows);
        var invalidAnswer = XlsxQuestionParser.Parse(invalidAnswerWorkbook);
        Check(invalidAnswer.IsSuccess
            && invalidAnswer.Rows[0].Errors.Any(error => error.Contains("正确答案", StringComparison.Ordinal)), "invalid answer is a row error");

        var duplicateRows = CreateRows();
        duplicateRows[1][0] = duplicateRows[0][0];
        using var duplicateWorkbook = CreateWorkbook(duplicateRows);
        var duplicates = XlsxQuestionParser.Parse(duplicateWorkbook);
        Check(duplicates.IsSuccess
            && duplicates.Rows[0].Errors.Any(error => error.Contains("重复", StringComparison.Ordinal))
            && duplicates.Rows[1].Errors.Any(error => error.Contains("重复", StringComparison.Ordinal)), "duplicate codes mark every row");

        var invalidHeader = XlsxImportRules.HeaderNames.ToArray();
        invalidHeader[1] = "题型X";
        using var invalidHeaderWorkbook = CreateWorkbook(CreateRows(), invalidHeader);
        var badHeader = XlsxQuestionParser.Parse(invalidHeaderWorkbook);
        Check(!badHeader.IsSuccess && badHeader.ErrorCode == "HEADER_MISMATCH", "invalid header label is fatal");

        using var formulaWorkbook = CreateWorkbook(CreateRows(), formula: true);
        var formula = XlsxQuestionParser.Parse(formulaWorkbook);
        Check(!formula.IsSuccess && formula.ErrorCode == "FORMULAS_NOT_ALLOWED", "formula is fatal");

        var tooManyRows = Enumerable.Range(0, 5001).Select(_ =>
        {
            var row = Enumerable.Repeat("", 14).ToArray();
            row[0] = "x";
            return row;
        }).ToArray();
        using var tooManyWorkbook = CreateWorkbook(tooManyRows);
        var tooMany = XlsxQuestionParser.Parse(tooManyWorkbook);
        Check(!tooMany.IsSuccess && tooMany.ErrorCode == "TOO_MANY_ROWS", "5001 data rows are fatal");

        Console.WriteLine("Import parser self-check passed: XLSX structure, answers, row validation, formulas, duplicates, and row limits.");
    }

    private static string[][] CreateRows() =>
    [
        ["S-1", "单选", "共享字符串题干", "7", "9", "", "", "", "", "A", "5", "易", "数学,代数", "解析"],
        ["M-1", "多选", "多选题干", "甲", "乙", "丙", "", "", "", "C，A; A C", "2.50", "中", "代数、数学,代数", ""],
        ["T-1", "判断", "内联字符串题干", "ignored", "", "", "", "", "", "FALSE", "1", "", "", ""]
    ];

    private static MemoryStream CreateWorkbook(
        IReadOnlyList<string[]> dataRows,
        string[]? header = null,
        bool formula = false)
    {
        var sharedStrings = new List<string>();
        var sharedIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        string Shared(string value)
        {
            if (!sharedIndexes.TryGetValue(value, out var index))
            {
                index = sharedStrings.Count;
                sharedIndexes.Add(value, index);
                sharedStrings.Add(value);
            }

            return index.ToString(CultureInfo.InvariantCulture);
        }

        var sheetData = new XElement(SpreadsheetNamespace + "sheetData");
        var headerRow = new XElement(SpreadsheetNamespace + "row", new XAttribute("r", 1));
        var headers = header ?? XlsxImportRules.HeaderNames;
        for (var i = 0; i < headers.Length; i++)
        {
            headerRow.Add(Cell($"{ColumnName(i + 1)}1", sharedIndex: Shared(headers[i])));
        }

        sheetData.Add(headerRow);
        for (var rowIndex = 0; rowIndex < dataRows.Count; rowIndex++)
        {
            var rowNo = rowIndex + 2;
            var row = new XElement(SpreadsheetNamespace + "row", new XAttribute("r", rowNo));
            var values = dataRows[rowIndex];
            for (var column = 0; column < values.Length; column++)
            {
                var value = values[column];
                if (value.Length == 0) continue;
                var reference = $"{ColumnName(column + 1)}{rowNo}";
                if (column + 1 == 11
                    && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
                {
                    row.Add(new XElement(SpreadsheetNamespace + "c",
                        new XAttribute("r", reference),
                        new XElement(SpreadsheetNamespace + "v", value)));
                }
                else if (rowNo == 3 && column + 1 == 3)
                {
                    row.Add(new XElement(SpreadsheetNamespace + "c",
                        new XAttribute("r", reference),
                        new XAttribute("t", "inlineStr"),
                        new XElement(SpreadsheetNamespace + "is",
                            new XElement(SpreadsheetNamespace + "t", value))));
                }
                else
                {
                    row.Add(Cell(reference, sharedIndex: Shared(value)));
                }
            }

            sheetData.Add(row);
        }

        var worksheet = new XDocument(new XElement(SpreadsheetNamespace + "worksheet", sheetData));
        if (formula)
        {
            var scoreCell = worksheet.Descendants(SpreadsheetNamespace + "c")
                .Single(cell => cell.Attribute("r")?.Value == "K2");
            scoreCell.AddFirst(new XElement(SpreadsheetNamespace + "f", "1+1"));
        }

        var sharedStringsDocument = new XDocument(new XElement(SpreadsheetNamespace + "sst",
            new XAttribute("count", sharedStrings.Count),
            new XAttribute("uniqueCount", sharedStrings.Count),
            sharedStrings.Select(value => new XElement(SpreadsheetNamespace + "si",
                new XElement(SpreadsheetNamespace + "t", value)))));
        var workbook = new XDocument(new XElement(SpreadsheetNamespace + "workbook",
            new XElement(SpreadsheetNamespace + "sheets",
                new XElement(SpreadsheetNamespace + "sheet",
                    new XAttribute("name", "题目"),
                    new XAttribute("sheetId", 1),
                    new XAttribute(OfficeRelationshipNamespace + "id", "rId1")))));
        var workbookRelationships = new XDocument(new XElement(PackageRelationshipNamespace + "Relationships",
            new XElement(PackageRelationshipNamespace + "Relationship",
                new XAttribute("Id", "rId1"),
                new XAttribute("Type", OfficeRelationshipNamespace.NamespaceName + "/worksheet"),
                new XAttribute("Target", "worksheets/sheet1.xml")),
            new XElement(PackageRelationshipNamespace + "Relationship",
                new XAttribute("Id", "rId2"),
                new XAttribute("Type", OfficeRelationshipNamespace.NamespaceName + "/sharedStrings"),
                new XAttribute("Target", "sharedStrings.xml"))));
        var packageRelationships = new XDocument(new XElement(PackageRelationshipNamespace + "Relationships",
            new XElement(PackageRelationshipNamespace + "Relationship",
                new XAttribute("Id", "rId1"),
                new XAttribute("Type", OfficeRelationshipNamespace.NamespaceName + "/officeDocument"),
                new XAttribute("Target", "xl/workbook.xml"))));
        var contentTypes = new XDocument(new XElement(ContentTypeNamespace + "Types",
            new XElement(ContentTypeNamespace + "Default",
                new XAttribute("Extension", "rels"),
                new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(ContentTypeNamespace + "Default",
                new XAttribute("Extension", "xml"),
                new XAttribute("ContentType", "application/xml")),
            new XElement(ContentTypeNamespace + "Override",
                new XAttribute("PartName", "/xl/workbook.xml"),
                new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
            new XElement(ContentTypeNamespace + "Override",
                new XAttribute("PartName", "/xl/worksheets/sheet1.xml"),
                new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")),
            new XElement(ContentTypeNamespace + "Override",
                new XAttribute("PartName", "/xl/sharedStrings.xml"),
                new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"))));

        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry("[Content_Types].xml", contentTypes);
            AddEntry("_rels/.rels", packageRelationships);
            AddEntry("xl/workbook.xml", workbook);
            AddEntry("xl/_rels/workbook.xml.rels", workbookRelationships);
            AddEntry("xl/sharedStrings.xml", sharedStringsDocument);
            AddEntry("xl/worksheets/sheet1.xml", worksheet);

            void AddEntry(string name, XDocument document)
            {
                var entry = archive.CreateEntry(name);
                using var entryStream = entry.Open();
                document.Save(entryStream);
            }
        }

        stream.Position = 0;
        return stream;
    }

    private static XElement Cell(string reference, string sharedIndex)
        => new(SpreadsheetNamespace + "c",
            new XAttribute("r", reference),
            new XAttribute("t", "s"),
            new XElement(SpreadsheetNamespace + "v", sharedIndex));

    private static string ColumnName(int column) => ((char)('A' + column - 1)).ToString();

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"Import parser self-check failed: {name}");
    }
}
