using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Verso.Rendering.Binding;

namespace Verso.Application.Executions;

/// <summary>A problem found while parsing. Messages say what is wrong and where (line, row, setting); they never echo content.</summary>
public sealed record ParseIssue(string Code, string Message);

/// <summary>The parsed record, or the reason there is none.</summary>
public sealed record ParseResult(JsonElement? Data, string? Format, ParseIssue? Issue);

/// <summary>
/// The Parse step (prototype: "Parse JSON / XML / CSV"). Reads raw file content from a field of the submitted data and
/// replaces the run's data with one structured record, so later steps (Validate, Generate PDF) see the same shape they
/// would from an API trigger.
/// <list type="bullet">
/// <item>JSON: used as is.</item>
/// <item>XML: the root element's name is kept as the top-level property. Child elements become properties (repeated names become
/// an array), attributes become "@name", and text next to attributes becomes "#text". Namespaces are dropped; DTDs are refused.</item>
/// <item>CSV: the first row names the columns; the delimiter (comma, semicolon, tab or pipe) is detected; each row is an object.</item>
/// </list>
/// XML and CSV carry only text, so values that are plain decimal numbers can be read as numbers (setting "numbers").
/// One run makes one document: the record must be a single object, so a list is accepted only when it has exactly one entry.
/// </summary>
public static partial class RecordParser
{
    public const string Auto = "auto";
    public const string Json = "json";
    public const string Xml = "xml";
    public const string Csv = "csv";
    public const string DefaultSource = "content";
    public const int MaxCsvRows = 10_000;
    public const int MaxDepth = 32;

    public static readonly IReadOnlyDictionary<string, string> Formats = new Dictionary<string, string>
    {
        [Auto] = "Auto-detect",
        [Json] = "JSON",
        [Xml] = "XML",
        [Csv] = "CSV",
    };

    /// <summary>UTF-8 content is the field's text itself. The others are single-byte code pages: the field then holds the file's bytes as base64.</summary>
    public static readonly IReadOnlyDictionary<string, string> Encodings = new Dictionary<string, string>
    {
        ["utf-8"] = "UTF-8",
        ["windows-1253"] = "Windows-1253 (Greek)",
        ["iso-8859-1"] = "ISO-8859-1",
    };

    private static readonly char[] Delimiters = [',', ';', '\t', '|'];

    public static ParseResult Parse(JsonElement config, JsonElement data)
    {
        var source = Setting(config, "source", DefaultSource);
        var format = Setting(config, "format", Auto);
        var encoding = Setting(config, "encoding", "utf-8");
        var recordPath = Setting(config, "recordPath", "");
        var numbers = !config.TryGetProperty("numbers", out var n) || n.ValueKind != JsonValueKind.False;

        if (!DataPath.TryResolve(data, source, out var field) || field.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(field.GetString()))
        {
            return Fail("parse.no_content", $"There is no text at '{source}' in the submitted data.");
        }

        var text = Decode(field.GetString()!, encoding);
        if (text is null)
        {
            return Fail("parse.encoding", $"The value at '{source}' isn't valid base64, which is how {Encodings.GetValueOrDefault(encoding, encoding)} content is sent.");
        }

        text = text.TrimStart('﻿').Trim();
        if (text.Length == 0)
        {
            return Fail("parse.no_content", $"The content at '{source}' is empty.");
        }

        if (format == Auto)
        {
            format = text[0] is '{' or '[' ? Json : text[0] == '<' ? Xml : Csv;
        }

        JsonNode? root;
        try
        {
            root = format switch
            {
                Json => JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = MaxDepth }),
                Xml => FromXml(text, numbers),
                Csv => FromCsv(text, numbers),
                _ => throw new ParseException("parse.format", $"Unknown format '{format}'."),
            };
        }
        catch (ParseException ex)
        {
            return Fail(ex.Code, ex.Message);
        }
        catch (JsonException ex)
        {
            return Fail("parse.invalid", $"The content isn't valid JSON{Where(ex.LineNumber, ex.BytePositionInLine)}.");
        }

        var node = root;
        if (recordPath.Length > 0)
        {
            foreach (var segment in recordPath.Split('.'))
            {
                node = node is JsonObject o && o.TryGetPropertyValue(segment, out var child) ? child : null;
            }

            if (node is null)
            {
                return Fail("parse.record_not_found", $"Nothing was found at the record path '{recordPath}'.");
            }
        }

        if (node is JsonArray list)
        {
            if (list.Count != 1)
            {
                return Fail("parse.not_single", $"The content holds {list.Count} records; a run handles one. {(recordPath.Length == 0 ? "Use a record path, or send one record per run." : "Send one record per run.")}");
            }

            node = list[0];
        }

        if (node is not JsonObject record)
        {
            return Fail("parse.not_record", "The content isn't a record (an object with named fields).");
        }

        return new ParseResult(JsonSerializer.SerializeToElement(record), format, null);
    }

    private static ParseResult Fail(string code, string message) => new(null, null, new ParseIssue(code, message));

    private static string Setting(JsonElement config, string name, string fallback) =>
        config.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : fallback;

    private static string Where(long? line, long? position) =>
        line is { } l ? $" (line {l + 1}, position {(position ?? 0) + 1})" : "";

    private static string? Decode(string value, string encoding)
    {
        if (encoding == "utf-8")
        {
            return value;
        }

        try
        {
            var bytes = Convert.FromBase64String(value);
            return encoding switch
            {
                "iso-8859-1" => Encoding.Latin1.GetString(bytes),
                // The code page provider ships with the shared framework; it only needs asking for by name.
                _ => (CodePagesEncodingProvider.Instance.GetEncoding(encoding) ?? Encoding.UTF8).GetString(bytes),
            };
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private sealed class ParseException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    // XML ------------------------------------------------------------------------------------------------------------

    private static JsonObject FromXml(string text, bool numbers)
    {
        XDocument document;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreProcessingInstructions = true };
            using var reader = XmlReader.Create(new StringReader(text), settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new ParseException("parse.invalid", $"The content isn't valid XML{Where(ex.LineNumber - 1, ex.LinePosition - 1)}.");
        }

        var root = document.Root ?? throw new ParseException("parse.invalid", "The content isn't valid XML.");
        return new JsonObject { [root.Name.LocalName] = Element(root, numbers, 1) };
    }

    private static JsonNode? Element(XElement element, bool numbers, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new ParseException("parse.too_deep", $"The XML is nested more than {MaxDepth} levels deep.");
        }

        var attributes = element.Attributes().Where(a => !a.IsNamespaceDeclaration).ToList();
        var children = element.Elements().ToList();
        if (attributes.Count == 0 && children.Count == 0)
        {
            return Scalar(element.Value.Trim(), numbers);
        }

        var result = new JsonObject();
        foreach (var attribute in attributes)
        {
            result["@" + attribute.Name.LocalName] = Scalar(attribute.Value, numbers);
        }

        foreach (var group in children.GroupBy(c => c.Name.LocalName))
        {
            var items = group.ToList();
            if (items.Count == 1)
            {
                result[group.Key] = Element(items[0], numbers, depth + 1);
            }
            else
            {
                var array = new JsonArray();
                foreach (var item in items)
                {
                    array.Add(Element(item, numbers, depth + 1));
                }

                result[group.Key] = array;
            }
        }

        if (children.Count == 0)
        {
            result["#text"] = Scalar(element.Value.Trim(), numbers);
        }

        return result;
    }

    // CSV ------------------------------------------------------------------------------------------------------------

    private static JsonArray FromCsv(string text, bool numbers)
    {
        var rows = ReadCsv(text, DetectDelimiter(text));
        if (rows.Count == 0)
        {
            throw new ParseException("parse.invalid", "The CSV has no rows.");
        }

        var header = rows[0].Select(h => h.Trim()).ToList();
        if (header.Any(h => h.Length == 0) || header.Distinct(StringComparer.Ordinal).Count() != header.Count)
        {
            throw new ParseException("parse.invalid", "The CSV's first row must name every column once.");
        }

        if (rows.Count - 1 > MaxCsvRows)
        {
            throw new ParseException("parse.too_large", $"The CSV has more than {MaxCsvRows:N0} rows.");
        }

        var result = new JsonArray();
        for (var r = 1; r < rows.Count; r++)
        {
            if (rows[r].Count != header.Count)
            {
                throw new ParseException("parse.invalid", $"CSV row {r + 1} has {rows[r].Count} columns, the header has {header.Count}.");
            }

            var record = new JsonObject();
            for (var c = 0; c < header.Count; c++)
            {
                record[header[c]] = Scalar(rows[r][c].Trim(), numbers);
            }

            result.Add(record);
        }

        return result;
    }

    /// <summary>The delimiter that appears most often in the first line, outside quotes; comma when none does.</summary>
    private static char DetectDelimiter(string text)
    {
        var counts = Delimiters.ToDictionary(d => d, _ => 0);
        var quoted = false;
        foreach (var ch in text)
        {
            if (ch == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && ch is '\r' or '\n')
            {
                break;
            }
            else if (!quoted && counts.TryGetValue(ch, out var seen))
            {
                counts[ch] = seen + 1;
            }
        }

        return Delimiters.OrderByDescending(d => counts[d]).First(); // OrderBy is stable: ties keep the comma first
    }

    /// <summary>RFC 4180 records: quoted fields may hold delimiters, line breaks and doubled quotes. Blank lines are skipped.</summary>
    private static List<List<string>> ReadCsv(string text, char delimiter)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var fieldStarted = false;

        void EndRow()
        {
            row.Add(field.ToString());
            field.Clear();
            fieldStarted = false;
            if (row.Count > 1 || row[0].Length > 0)
            {
                rows.Add(row);
            }

            row = [];
        }

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch != '"')
                {
                    field.Append(ch);
                }
                else if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                {
                    quoted = false;
                }
            }
            else if (ch == '"' && !fieldStarted)
            {
                quoted = true;
                fieldStarted = true;
            }
            else if (ch == delimiter)
            {
                row.Add(field.ToString());
                field.Clear();
                fieldStarted = false;
            }
            else if (ch is '\r' or '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                EndRow();
            }
            else
            {
                field.Append(ch);
                fieldStarted = true;
            }
        }

        if (quoted)
        {
            throw new ParseException("parse.invalid", "The CSV has a quoted value that never ends.");
        }

        if (fieldStarted || field.Length > 0 || row.Count > 0)
        {
            EndRow();
        }

        return rows;
    }

    // Values ---------------------------------------------------------------------------------------------------------

    /// <summary>Plain decimals only (no leading zeros, no exponent, at most 15 digits), so identifiers such as "007" stay text.</summary>
    private static JsonValue Scalar(string text, bool numbers) =>
        numbers && text.Length <= 16 && PlainNumber().IsMatch(text) ? JsonValue.Create(decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture))! : JsonValue.Create(text)!;

    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?$")]
    private static partial Regex PlainNumber();
}
