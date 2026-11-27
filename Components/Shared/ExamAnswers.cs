using System.Text.Json;

namespace ExamOps.Components.Shared;

public static class ExamAnswers
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string? Single(string? label) => label is null ? null : JsonSerializer.Serialize(new { value = label }, JsonOptions);
    public static string? Multiple(IEnumerable<string> labels) => labels is null ? null : JsonSerializer.Serialize(new { values = labels.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray() }, JsonOptions);
    public static string? TrueFalse(bool? value) => value is null ? null : JsonSerializer.Serialize(new { value }, JsonOptions);

    public static string? ReadSingle(string? json) => Read(json, "value", element => element.ValueKind == JsonValueKind.String ? element.GetString() : null);
    public static string[] ReadMultiple(string? json)
    {
        JsonDocument? document = null;
        try { if (!string.IsNullOrWhiteSpace(json)) document = JsonDocument.Parse(json); }
        catch (JsonException) { return []; }
        using (document)
        {
            if (document is null || !document.RootElement.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array) return [];
            return values.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray();
        }
    }
    public static bool? ReadTrueFalse(string? json) => Read<bool?>(json, "value", element => element.ValueKind is JsonValueKind.True or JsonValueKind.False ? element.GetBoolean() : null);

    private static T? Read<T>(string? json, string property, Func<JsonElement, T?> read)
    {
        JsonDocument? document = null;
        try { if (!string.IsNullOrWhiteSpace(json)) document = JsonDocument.Parse(json); }
        catch (JsonException) { return default; }
        using (document)
        {
            if (document is null || !document.RootElement.TryGetProperty(property, out var element)) return default;
            return read(element);
        }
    }

    public static void RunSelfCheck()
    {
        if (ReadSingle(Single("B")) != "B" || ReadTrueFalse(TrueFalse(false)) != false
            || !ReadMultiple(Multiple(["B", "A", "A"])).SequenceEqual(["A", "B"])
            || ReadSingle("not-json") is not null || ReadMultiple("{}").Count() != 0)
            throw new InvalidOperationException("考生答案绑定或恢复自查失败。");
    }
}
