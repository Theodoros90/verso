namespace Verso.Application.Uploads;

/// <summary>What a workflow's File upload step accepts, for the upload dialog: file name extensions (with the dot) and the largest file in bytes.</summary>
public sealed record UploadSettingsDto(IReadOnlyList<string> Accept, long MaxBytes);

/// <summary>Upload settings (ADR 0023). The ceiling is read from <c>Limits:MaxUploadBytes</c>, the retention from <c>Uploads:Retention</c>.</summary>
public sealed class UploadOptions
{
    public const string Section = "Uploads";

    /// <summary>The largest file the server takes, whatever the step says.</summary>
    public long CeilingBytes { get; set; } = UploadRules.DefaultCeilingBytes;

    /// <summary>How long an upload no run has taken is kept before the Worker expires it.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);
}
