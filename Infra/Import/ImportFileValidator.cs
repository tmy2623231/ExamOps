using System.IO.Compression;
using System.Xml;

namespace ExamOps.Infra.Import;

public sealed record ImportFileValidationResult(bool IsSuccess, string Code, string? Error);

public static class ImportFileValidator
{
    public const long MaxFileSizeBytes = 10 * 1024 * 1024;
    public const long MaxExpandedWorksheetXmlBytes = 20 * 1024 * 1024;
    public const long MaxTotalExpandedBytes = 40 * 1024 * 1024;
    private const int MaxZipEntries = 256;

    public static ImportFileValidationResult Validate(Stream stream, string fileName)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return Fail("INVALID_EXTENSION", "只允许上传.xlsx文件。");
        }

        var result = Prepare(stream, out var package);
        package?.Dispose();
        return result;
    }

    internal static ImportFileValidationResult Prepare(Stream stream, out MemoryStream? package)
    {
        ArgumentNullException.ThrowIfNull(stream);
        package = null;
        MemoryStream? buffer = null;
        var initialPosition = stream.CanSeek ? stream.Position : 0;
        try
        {
            if (stream.CanSeek)
            {
                var available = stream.Length - initialPosition;
                if (available < 0) return Fail("INVALID_ZIP", "文件流位置无效。");
                if (available > MaxFileSizeBytes) return Fail("FILE_TOO_LARGE", "文件不能超过10MiB。");
            }

            buffer = new MemoryStream();
            if (!TryCopyBounded(stream, buffer, MaxFileSizeBytes, out var length))
            {
                return Fail("FILE_TOO_LARGE", "文件不能超过10MiB。");
            }

            var bytes = buffer.GetBuffer();
            if (length < 2 || bytes[0] != (byte)'P' || bytes[1] != (byte)'K')
            {
                return Fail("INVALID_ZIP", "文件不是有效的ZIP/OOXML文件。");
            }

            buffer.Position = 0;
            var result = ValidateZip(buffer);
            if (!result.IsSuccess) return result;

            package = buffer;
            buffer = null;
            return result;
        }
        catch (XmlException)
        {
            return Fail("INVALID_XML", "XLSX中包含不安全或无效的XML。");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or ArgumentException)
        {
            return Fail("INVALID_ZIP", "文件不是有效的ZIP/OOXML文件。");
        }
        finally
        {
            buffer?.Dispose();
            if (stream.CanSeek) stream.Position = initialPosition;
        }
    }

    private static ImportFileValidationResult ValidateZip(Stream package)
    {
        using var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        var entries = archive.Entries.ToArray();
        if (entries.Length > MaxZipEntries)
        {
            return Fail("TOO_MANY_ZIP_ENTRIES", "XLSX包含的ZIP条目过多。");
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!IsSafeEntryName(entry.FullName) || !names.Add(entry.FullName))
            {
                return Fail("UNSAFE_ZIP_ENTRY", "XLSX包含不安全或重复的ZIP条目。");
            }
        }

        if (archive.GetEntry("[Content_Types].xml") is null)
        {
            return Fail("MISSING_CONTENT_TYPES", "XLSX缺少[Content_Types].xml。");
        }

        if (entries.All(entry => entry.FullName != "xl/workbook.xml"))
        {
            return Fail("MISSING_WORKBOOK", "XLSX缺少xl/workbook.xml。");
        }

        if (entries.All(entry => entry.FullName != "xl/worksheets/sheet1.xml"))
        {
            return Fail("MISSING_WORKSHEET", "XLSX缺少xl/worksheets/sheet1.xml。");
        }

        foreach (var entry in entries)
        {
            var name = entry.FullName;
            if (entry.Name.Equals("vbaProject.bin", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("xl/macrosheets/", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("xl/activeX/", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("xl/ctrlProps/", StringComparison.OrdinalIgnoreCase))
            {
                return Fail("MACROS_NOT_ALLOWED", "XLSX不能包含宏、宏工作表或ActiveX控件。");
            }

            if (name.StartsWith("xl/externalLinks/", StringComparison.OrdinalIgnoreCase))
            {
                return Fail("EXTERNAL_LINKS_NOT_ALLOWED", "XLSX不能包含外部链接。");
            }

            if (name.StartsWith("xl/embeddings/", StringComparison.OrdinalIgnoreCase))
            {
                return Fail("EMBEDDINGS_NOT_ALLOWED", "XLSX不能包含OLE嵌入对象。");
            }
        }

        // 校验全部 ZIP 条目累计解压预算，防止多条目压缩炸弹耗尽 CPU 与内存。
        long totalExpanded = 0;
        var scratch = new byte[8192];
        foreach (var entry in entries)
        {
            if (entry.Length > MaxExpandedWorksheetXmlBytes)
            {
                return Fail("XML_TOO_LARGE", "XLSX的单个ZIP条目超过大小限制。");
            }
            using var entryStream = entry.Open();
            long entryBytes = 0;
            int read;
            while ((read = entryStream.Read(scratch, 0, scratch.Length)) > 0)
            {
                entryBytes += read;
                totalExpanded += read;
                if (entryBytes > MaxExpandedWorksheetXmlBytes || totalExpanded > MaxTotalExpandedBytes)
                {
                    return Fail("XML_TOO_LARGE", "XLSX解压后总大小超过限制。");
                }
            }
        }

        var contentTypes = archive.GetEntry("[Content_Types].xml")!;
        var contentTypesResult = ScanContentTypes(contentTypes);
        if (!contentTypesResult.IsSuccess) return contentTypesResult;

        foreach (var entry in entries.Where(entry => entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
        {
            var result = ScanRelationships(entry, entry.FullName.Equals("xl/_rels/workbook.xml.rels", StringComparison.Ordinal));
            if (!result.IsSuccess) return result;
        }

        foreach (var entry in entries.Where(IsWorksheetXml))
        {
            var result = ScanWorksheet(entry);
            if (!result.IsSuccess) return result;
        }

        foreach (var requiredXml in new[] { "xl/workbook.xml", "xl/sharedStrings.xml" })
        {
            var entry = archive.GetEntry(requiredXml);
            if (entry is null) continue;
            var result = ScanPlainXml(entry);
            if (!result.IsSuccess) return result;
        }

        return new ImportFileValidationResult(true, "OK", null);
    }

    private static ImportFileValidationResult ScanContentTypes(ZipArchiveEntry entry)
    {
        if (!TryReadEntry(entry, MaxExpandedWorksheetXmlBytes, out var xml) || xml is null)
        {
            return Fail("XML_TOO_LARGE", "XLSX的XML部件超过大小限制。");
        }

        using (xml)
        {
            try
            {
                using var reader = XmlReader.Create(xml, CreateXmlReaderSettings());
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element) continue;
                    var contentType = reader.GetAttribute("ContentType");
                    if (contentType is null) continue;
                    if (contentType.Contains("macroEnabled", StringComparison.OrdinalIgnoreCase)
                        || contentType.Contains("vbaProject", StringComparison.OrdinalIgnoreCase))
                    {
                        return Fail("MACROS_NOT_ALLOWED", "XLSX不能包含宏内容。");
                    }

                    if (contentType.Contains("externalLink", StringComparison.OrdinalIgnoreCase))
                    {
                        return Fail("EXTERNAL_LINKS_NOT_ALLOWED", "XLSX不能包含外部链接。");
                    }

                    if (contentType.Contains("oleObject", StringComparison.OrdinalIgnoreCase))
                    {
                        return Fail("EMBEDDINGS_NOT_ALLOWED", "XLSX不能包含OLE嵌入对象。");
                    }

                    if (contentType.Contains("dde", StringComparison.OrdinalIgnoreCase))
                    {
                        return Fail("DDE_NOT_ALLOWED", "XLSX不能包含DDE链接。");
                    }
                }
            }
            catch (XmlException)
            {
                return Fail("INVALID_XML", "XLSX中包含不安全或无效的XML。");
            }
        }

        return new ImportFileValidationResult(true, "OK", null);
    }

    private static ImportFileValidationResult ScanRelationships(ZipArchiveEntry entry, bool rejectAnyExternalTarget)
    {
        if (!TryReadEntry(entry, MaxExpandedWorksheetXmlBytes, out var xml) || xml is null)
        {
            return Fail("XML_TOO_LARGE", "XLSX的XML部件超过大小限制。");
        }

        using (xml)
        {
            try
            {
                using var reader = XmlReader.Create(xml, CreateXmlReaderSettings());
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Relationship") continue;
                    var type = reader.GetAttribute("Type") ?? "";
                    if (type.EndsWith("/externalLink", StringComparison.OrdinalIgnoreCase))
                    {
                        return Fail("EXTERNAL_LINKS_NOT_ALLOWED", "XLSX不能包含外部链接。");
                    }

                    if (type.EndsWith("/oleObject", StringComparison.OrdinalIgnoreCase)
                        || type.EndsWith("/package", StringComparison.OrdinalIgnoreCase))
                    {
                        return Fail("EMBEDDINGS_NOT_ALLOWED", "XLSX不能包含OLE嵌入对象。");
                    }

                    if (type.EndsWith("/macrosheet", StringComparison.OrdinalIgnoreCase))
                    {
                        return Fail("MACROS_NOT_ALLOWED", "XLSX不能包含宏工作表。");
                    }

                    if (type.EndsWith("/ddeLink", StringComparison.OrdinalIgnoreCase))
                    {
                        return Fail("DDE_NOT_ALLOWED", "XLSX不能包含DDE链接。");
                    }

                    if (rejectAnyExternalTarget && string.Equals(reader.GetAttribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase))
                    {
                        return Fail("EXTERNAL_LINKS_NOT_ALLOWED", "XLSX不能包含外部链接。");
                    }
                }
            }
            catch (XmlException)
            {
                return Fail("INVALID_XML", "XLSX中包含不安全或无效的XML。");
            }
        }

        return new ImportFileValidationResult(true, "OK", null);
    }

    private static ImportFileValidationResult ScanWorksheet(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxExpandedWorksheetXmlBytes
            || !TryReadEntry(entry, MaxExpandedWorksheetXmlBytes, out var xml)
            || xml is null)
        {
            return Fail("WORKSHEET_TOO_LARGE", "工作表XML展开后不能超过20MiB。");
        }

        using (xml)
        {
            try
            {
                using var reader = XmlReader.Create(xml, CreateXmlReaderSettings());
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element) continue;
                    if (reader.LocalName == "f")
                    {
                        return Fail("FORMULAS_NOT_ALLOWED", "XLSX不能包含公式单元格。");
                    }

                    if (reader.LocalName is "ddeLink" or "dde")
                    {
                        return Fail("DDE_NOT_ALLOWED", "XLSX不能包含DDE链接。");
                    }

                    if (reader.LocalName == "oleObject")
                    {
                        return Fail("EMBEDDINGS_NOT_ALLOWED", "XLSX不能包含OLE嵌入对象。");
                    }
                }
            }
            catch (XmlException)
            {
                return Fail("INVALID_XML", "XLSX中包含不安全或无效的XML。");
            }
        }

        return new ImportFileValidationResult(true, "OK", null);
    }

    private static ImportFileValidationResult ScanPlainXml(ZipArchiveEntry entry)
    {
        if (!TryReadEntry(entry, MaxExpandedWorksheetXmlBytes, out var xml) || xml is null)
        {
            return Fail("XML_TOO_LARGE", "XLSX的XML部件超过大小限制。");
        }

        using (xml)
        {
            try
            {
                using var reader = XmlReader.Create(xml, CreateXmlReaderSettings());
                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element && reader.LocalName is "ddeLink" or "dde")
                    {
                        return Fail("DDE_NOT_ALLOWED", "XLSX不能包含DDE链接。");
                    }
                }
            }
            catch (XmlException)
            {
                return Fail("INVALID_XML", "XLSX中包含不安全或无效的XML。");
            }
        }

        return new ImportFileValidationResult(true, "OK", null);
    }

    internal static XmlReaderSettings CreateXmlReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        ConformanceLevel = ConformanceLevel.Document,
        MaxCharactersInDocument = MaxExpandedWorksheetXmlBytes,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true
    };

    internal static bool TryReadEntry(ZipArchiveEntry entry, long maxBytes, out MemoryStream? stream)
    {
        stream = null;
        if (entry.Length > maxBytes) return false;
        var buffer = new MemoryStream();
        try
        {
            using var source = entry.Open();
            if (!TryCopyBounded(source, buffer, maxBytes, out _))
            {
                buffer.Dispose();
                return false;
            }

            buffer.Position = 0;
            stream = buffer;
            return true;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    private static bool TryCopyBounded(Stream source, Stream destination, long maxBytes, out long length)
    {
        length = 0;
        var buffer = new byte[81920];
        while (length <= maxBytes)
        {
            var count = (int)Math.Min(buffer.Length, maxBytes + 1 - length);
            if (count == 0) break;
            var read = source.Read(buffer, 0, count);
            if (read == 0) break;
            destination.Write(buffer, 0, read);
            length += read;
            if (length > maxBytes) return false;
        }

        return true;
    }

    private static bool IsSafeEntryName(string name)
    {
        if (string.IsNullOrEmpty(name)
            || name.StartsWith('/')
            || name.Contains('\\')
            || name.Contains('\0'))
        {
            return false;
        }

        var parts = name.Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            if (i == parts.Length - 1 && name.EndsWith('/') && parts[i].Length == 0) continue;
            if (parts[i].Length == 0 || parts[i] is "." or "..") return false;
        }

        return !parts[0].Contains(':');
    }

    private static bool IsWorksheetXml(ZipArchiveEntry entry)
        => entry.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase)
            && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

    private static ImportFileValidationResult Fail(string code, string error)
        => new(false, code, error);
}
