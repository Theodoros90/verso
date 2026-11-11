namespace Verso.Domain.Ingestion;

/// <summary>
/// A folder path a workflow author typed into a Folder input step. The key is derived from the path, so the same path
/// always has the same key, and runs and unfinished files (which store only keys) can always be resolved again, even
/// after the workflow changed. Registered when a workflow is published or a path is access-tested; never deleted,
/// because published workflows and ingestion records refer to it. The Worker reads this table and validates each
/// path under its own service identity (and its optional AllowedRoots).
/// </summary>
public sealed class TypedFolderPath
{
    private TypedFolderPath() { } // EF Core

    public string Key { get; private set; } = "";
    public string Path { get; private set; } = "";
    public bool AllowInput { get; private set; }
    public bool AllowOutput { get; private set; }
    public string FirstRegisteredBy { get; private set; } = "";
    public DateTimeOffset FirstRegisteredAt { get; private set; }

    public static TypedFolderPath Register(string key, string path, bool input, bool output, string actor, DateTimeOffset now) => new()
    {
        Key = key,
        Path = path,
        AllowInput = input,
        AllowOutput = output,
        FirstRegisteredBy = actor,
        FirstRegisteredAt = now,
    };

    /// <summary>The same path used for another purpose adds that purpose (a path is never narrowed).</summary>
    public bool AddPurpose(bool input, bool output)
    {
        var changed = (input && !AllowInput) || (output && !AllowOutput);
        AllowInput |= input;
        AllowOutput |= output;
        return changed;
    }
}
