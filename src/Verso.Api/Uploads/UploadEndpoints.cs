using System.Buffers;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Microsoft.OpenApi;
using Verso.Api.Auth;
using Verso.Api.Common;
using Verso.Application;
using Verso.Application.Executions;
using Verso.Application.Uploads;

namespace Verso.Api.Uploads;

/// <summary>
/// File upload (ADR 0023): a signed-in operator uploads one file to a workflow that starts with File upload, and a run starts.
/// Contract documented in docs/api.md#file-upload.
/// </summary>
public static class UploadEndpoints
{
    /// <summary>Rate-limit policy for uploads, per signed-in user (<c>Limits:UploadsPerMinute</c>).</summary>
    public const string UploadRateLimit = "uploads";

    /// <summary>Room for the multipart framing (boundaries, part headers) above the largest file.</summary>
    public const long MultipartOverheadBytes = 1_048_576;

    private const string FilePart = "file";
    private const int ChunkBytes = 81_920;

    public static IEndpointRouteBuilder MapUploadEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/workflows/{key}/uploads", UploadAsync)
            .WithTags("Uploads")
            .WithName("UploadFile")
            .RequireAuthorization(Policies.Operate)
            .RequireRateLimiting(UploadRateLimit)
            .DisableAntiforgery() // bearer-token API: no cookies, so no cross-site form posts to guard against
            // The body is described for the contract only. Accepts<T>() would also make routing answer 415 to any other content
            // type before sign-in is checked; the handler answers that itself, after authorization, as a 400.
            .AddOpenApiOperationTransformer((operation, _, _) =>
            {
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["multipart/form-data"] = new()
                        {
                            Schema = new OpenApiSchema
                            {
                                Type = JsonSchemaType.Object,
                                Required = new HashSet<string> { FilePart },
                                Properties = new Dictionary<string, IOpenApiSchema>
                                {
                                    [FilePart] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" },
                                },
                            },
                        },
                    },
                };
                return Task.CompletedTask;
            })
            .Produces<SubmissionDto>(StatusCodes.Status202Accepted)
            .Produces<SubmissionDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);

        app.MapGet("/api/v1/workflows/{key}/upload-settings", async (string key, UploadService service, CancellationToken ct) =>
                (await service.SettingsAsync(key, ct)).ToResult())
            .WithTags("Uploads")
            .WithName("GetUploadSettings")
            .RequireAuthorization(Policies.Operate)
            .Produces<UploadSettingsDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    /// <summary>
    /// In order: the workflow takes uploads (404 / 409, before a byte of the body is read); the request's body limit is raised from
    /// the global 2 MB to the ceiling + 1 MB; the multipart body holds exactly one part, a file named <c>file</c> (400); the file part
    /// is read into memory only up to the step's limit + 1 byte, and a larger one is refused at once without reading the rest (413);
    /// then the upload service checks and stages it and starts the run (202 new, 200 the same file again).
    /// </summary>
    private static async Task<IResult> UploadAsync(string key, UploadService service, IOptions<UploadOptions> options, HttpContext http, CancellationToken ct)
    {
        // Never more than min(step limit, ceiling) + 1 bytes of the file are held: the step's limit is known before reading.
        var settings = await service.SettingsAsync(key, ct);
        if (!settings.IsSuccess)
        {
            return settings.Error!.ToProblem();
        }

        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeLimit)
        {
            sizeLimit.MaxRequestBodySize = options.Value.CeilingBytes + MultipartOverheadBytes;
        }

        if (!MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var mediaType)
            || !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value is not { Length: > 0 and <= 70 } boundary)
        {
            return Invalid("Send the file as multipart/form-data with one part named 'file'.");
        }

        string? fileName = null;
        byte[]? content = null;
        try
        {
            var reader = new MultipartReader(boundary, http.Request.Body);
            while (await reader.ReadNextSectionAsync(ct) is { } section)
            {
                if (content is not null || !IsFilePart(section, out var name))
                {
                    return Invalid("Send exactly one part: the file, named 'file'. No other parts or fields.");
                }

                content = await ReadLimitedAsync(section.Body, settings.Value!.MaxBytes, ct);
                if (content is null)
                {
                    return TooLarge(await service.TooLargeAsync(key, ct));
                }

                fileName = name;
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return TooLarge(await service.TooLargeAsync(key, ct)); // the body is over the request limit (ceiling + 1 MB)
        }
        catch (InvalidDataException)
        {
            return Invalid("The multipart body is malformed.");
        }

        if (content is null)
        {
            return Invalid("Send exactly one part: the file, named 'file'.");
        }

        var result = await service.AcceptAsync(key, fileName!, content, http.Request.Headers[CorrelationId.Header].FirstOrDefault() ?? CorrelationId.Get(http), ct);
        if (!result.IsSuccess)
        {
            return result.Error!.Code == "upload.too_large" ? TooLarge(result.Error) : result.Error.ToProblem();
        }

        var submission = result.Value!;
        return submission.Created
            ? Results.Accepted($"/api/v1/executions/{submission.Execution.ExecutionId}", submission.Execution)
            : Results.Ok(submission.Execution);
    }

    /// <summary>A <c>form-data</c> part named <c>file</c> that carries a file name.</summary>
    private static bool IsFilePart(MultipartSection section, out string fileName)
    {
        fileName = "";
        if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
            || !disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(disposition.Name).Value != FilePart)
        {
            return false;
        }

        var name = disposition.FileNameStar.HasValue ? disposition.FileNameStar.Value : HeaderUtilities.RemoveQuotes(disposition.FileName).Value;
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        fileName = name;
        return true;
    }

    /// <summary>
    /// The whole stream when it holds at most <paramref name="maxBytes"/>, otherwise null. Never asks for more than
    /// <paramref name="maxBytes"/> + 1 bytes, so a larger file (or a body that never ends) is stopped right there.
    /// </summary>
    private static async Task<byte[]?> ReadLimitedAsync(Stream body, long maxBytes, CancellationToken ct)
    {
        var chunks = new List<(byte[] Buffer, int Count)>();
        long total = 0;
        try
        {
            while (true)
            {
                var buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);
                var read = await body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(ChunkBytes, maxBytes + 1 - total)), ct);
                if (read == 0)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    break;
                }

                chunks.Add((buffer, read));
                total += read;
                if (total > maxBytes)
                {
                    return null;
                }
            }

            var content = new byte[total];
            var offset = 0;
            foreach (var (buffer, count) in chunks)
            {
                buffer.AsSpan(0, count).CopyTo(content.AsSpan(offset));
                offset += count;
            }

            return content;
        }
        finally
        {
            foreach (var (buffer, _) in chunks)
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true); // file bytes never linger in the shared pool
            }
        }
    }

    private static IResult Invalid(string message) =>
        AppError.Validation(new Dictionary<string, string[]> { [FilePart] = [message] }).ToProblem();

    private static IResult TooLarge(AppError error) =>
        error.Code == "upload.too_large"
            ? Results.Problem(detail: error.Message, statusCode: StatusCodes.Status413PayloadTooLarge, extensions: new Dictionary<string, object?> { ["code"] = error.Code })
            : error.ToProblem(); // the workflow changed meanwhile (not found / not available)
}
