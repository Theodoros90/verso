using System.Text.Json;
using System.Text.Unicode;
using Verso.Application.Documents;

namespace Verso.Application.Uploads;

/// <summary>One accepted kind of uploaded file. The extension has a leading dot; the content type is what Verso records, never the client's.</summary>
public sealed record UploadType(string Extension, string ContentType, bool IsText);

/// <summary>Why an uploaded file was refused: a stable code and a message for the operator.</summary>
public sealed record UploadProblem(string Code, string Message);

/// <summary>
/// What the File upload step accepts (ADR 0023): a fixed list of types, checked by file name extension and by the first bytes of the content.
/// The content type the browser sends is never trusted.
/// </summary>
public static class UploadRules
{
    /// <summary>The largest upload the server takes, 25 MB. A step's "Largest file" can only be lower.</summary>
    public const long DefaultCeilingBytes = 26_214_400;

    private static readonly byte[] PdfHeader = "%PDF-"u8.ToArray();
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpgHeader = [0xFF, 0xD8, 0xFF];

    public static IReadOnlyDictionary<string, UploadType> Types { get; } = new Dictionary<string, UploadType>(StringComparer.Ordinal)
    {
        ["PDF"] = new(".pdf", "application/pdf", false),
        ["XML"] = new(".xml", "application/xml", true),
        ["CSV"] = new(".csv", "text/csv", true),
        ["JSON"] = new(".json", "application/json", true),
        ["TXT"] = new(".txt", "text/plain", true),
        ["PNG"] = new(".png", "image/png", false),
        ["JPG"] = new(".jpg", "image/jpeg", false),
    };

    private static readonly Dictionary<string, long> Sizes = new(StringComparer.Ordinal)
    {
        ["1mb"] = 1_048_576,
        ["10mb"] = 10_485_760,
        ["25mb"] = 26_214_400,
    };

    private static List<string> Tokens(string accept) =>
        [.. accept.Split(',').Select(t => t.Trim().ToUpperInvariant())];

    /// <summary>Design-time check of the step's "Accepted file types": a comma-separated list of known types, at least one, none twice. Null when fine.</summary>
    public static string? ValidateAccept(string? accept)
    {
        if (string.IsNullOrWhiteSpace(accept))
        {
            return "Choose at least one accepted file type.";
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in Tokens(accept))
        {
            if (token.Length == 0)
            {
                return "The accepted file types have an empty entry. Separate the types with single commas.";
            }

            if (!Types.ContainsKey(token))
            {
                return $"'{token}' is not an accepted file type. Choose from {string.Join(", ", Types.Keys)}.";
            }

            if (!seen.Add(token))
            {
                return $"'{token}' is listed more than once.";
            }
        }

        return null;
    }

    /// <summary>The types named by an already validated list. Unknown names are skipped.</summary>
    public static IReadOnlyList<UploadType> ParseAccept(string accept) =>
        [.. Tokens(accept).Distinct(StringComparer.Ordinal).Where(Types.ContainsKey).Select(t => Types[t])];

    /// <summary>The byte limit for a "Largest file" choice, never above <paramref name="ceiling"/>. An unknown choice (including the old <c>100mb</c>) gets the ceiling.</summary>
    public static long MaxBytes(string maxSizeKey, long ceiling) =>
        Sizes.TryGetValue(maxSizeKey ?? "", out var bytes) ? Math.Min(bytes, ceiling) : ceiling;

    /// <summary>Checks a file against the accepted types: not empty, its extension is accepted, and the content looks like what the extension says. Null when fine.</summary>
    public static UploadProblem? Check(string fileName, ReadOnlySpan<byte> content, IReadOnlyList<UploadType> accepted)
    {
        if (content.Length == 0)
        {
            return new("upload.empty", "The file is empty.");
        }

        var type = Match(fileName, accepted);
        if (type is null)
        {
            return new("upload.type_not_allowed", "This kind of file is not accepted here.");
        }

        var ok = type.Extension switch
        {
            ".pdf" => content.StartsWith(PdfHeader),
            ".png" => content.StartsWith(PngHeader),
            ".jpg" => content.StartsWith(JpgHeader),
            _ => IsPlainText(content, type.Extension == ".json"),
        };
        return ok ? null : new("upload.content_mismatch", "The content of the file does not match its file name extension.");
    }

    /// <summary>The accepted type a file name's extension names (case-insensitive; <c>.jpeg</c> counts as JPG), or null.</summary>
    public static UploadType? Match(string fileName, IReadOnlyList<UploadType> accepted)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        var dot = (fileName ?? "").LastIndexOf('.');
        var extension = dot >= 0 ? fileName![dot..] : "";
        return accepted.FirstOrDefault(t => string.Equals(t.Extension, extension, StringComparison.OrdinalIgnoreCase)
            // .jpeg is the same format as .jpg
            || (t.Extension == ".jpg" && string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Valid UTF-8 without NUL bytes and not another known kind of file; for JSON also exactly one strict JSON value (no comments,
    /// no trailing commas), after an optional UTF-8 byte-order mark. Works on the bytes only: no string is built, so a 25 MB file
    /// is checked without copying it.
    /// </summary>
    private static bool IsPlainText(ReadOnlySpan<byte> content, bool json)
    {
        // a file of another known kind hiding behind a text extension
        if (content.Contains((byte)0) || content.StartsWith(PdfHeader) || content.StartsWith(PngHeader) || content.StartsWith(JpgHeader))
        {
            return false;
        }

        if (!Utf8.IsValid(content))
        {
            return false;
        }

        return !json || IsSingleJsonValue(content.StartsWith(Utf8Bom) ? content[Utf8Bom.Length..] : content);
    }

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    private static bool IsSingleJsonValue(ReadOnlySpan<byte> utf8)
    {
        // The defaults of JsonDocument.Parse: comments and trailing commas refused, one value only, depth 64.
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
        try
        {
            while (reader.Read())
            {
                // only the structure is checked; the UTF-8 of the whole file was checked before
            }

            // Read() returned false: the whole input was one complete value (anything after it, or nothing at all, throws).
            return reader.BytesConsumed > 0 && reader.CurrentDepth == 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>A file name that is safe to store and show: plain characters, no path parts, at most 200 characters, always ending in <paramref name="extension"/>.</summary>
    public static string SafeFileName(string raw, string extension) =>
        DocumentNames.FileName((raw ?? "").Replace("..", "_", StringComparison.Ordinal), extension);
}
