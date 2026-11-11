using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.Server.IIS;
using Verso.Api.Audit;
using Verso.Api.Auth;
using Verso.Api.Common;
using Verso.Api.Documents;
using Verso.Api.Executions;
using Verso.Api.Health;
using Verso.Api.Ingestion;
using Verso.Api.Realtime;
using Verso.Api.Templates;
using Verso.Api.Workflows;
using Verso.Application;
using Verso.Infrastructure;
using Verso.Infrastructure.Documents;
using Verso.Infrastructure.Jobs;

var builder = WebApplication.CreateBuilder(args);

// Sign-in (plan §7): Entra ID access tokens, or synthetic users in Development/Testing only. Refuses to start when
// the configuration is unsafe (Development sign-in elsewhere, or Entra without tenant/client id).
builder.AddVersoAuth();

// Limits (plan §7). Request bodies are capped for every endpoint (the largest legitimate one is a template draft);
// submissions are additionally rate-limited per user.
var limits = builder.Configuration.GetSection("Limits");
var maxBodyBytes = limits.GetValue("MaxRequestBodyBytes", 2L * 1024 * 1024);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = maxBodyBytes);
builder.Services.Configure<IISServerOptions>(o => o.MaxRequestBodySize = maxBodyBytes);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(ExecutionEndpoints.SubmissionRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
        http.User.FindFirst(AuthSetup.IdClaim)?.Value ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limits.GetValue("SubmissionsPerMinute", 120),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
    options.OnRejected = (ctx, _) =>
    {
        ctx.HttpContext.Response.Headers.RetryAfter = "60";
        return ValueTask.CompletedTask;
    };
});

// Log provider and JSON format come from the "Logging" section of appsettings.json.
builder.Services.AddHttpLogging(options =>
{
    // Method, path, status and duration only. Never headers or bodies: they may carry tokens or payload data.
    options.LoggingFields = HttpLoggingFields.RequestMethod
                            | HttpLoggingFields.RequestPath
                            | HttpLoggingFields.ResponseStatusCode
                            | HttpLoggingFields.Duration;
    options.CombineLogs = true;
});

builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = ctx =>
{
    // Problems produced by the framework (sign-in, authorization, limits) get a stable code like ours.
    if (!ctx.ProblemDetails.Extensions.ContainsKey("code"))
    {
        var code = ctx.ProblemDetails.Status switch
        {
            StatusCodes.Status401Unauthorized => "unauthenticated",
            StatusCodes.Status403Forbidden => "forbidden",
            StatusCodes.Status413PayloadTooLarge => "request_too_large",
            StatusCodes.Status429TooManyRequests => "rate_limited",
            _ => null,
        };
        if (code is not null)
        {
            ctx.ProblemDetails.Extensions["code"] = code;
        }
    }
});

// The SPA is served from its own origin in production (docs/runbook.md); allow only the origins configured.
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
if (corsOrigins.Length > 0)
{
    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()
        .WithExposedHeaders("ETag", "Location", CorrelationId.Header)));
}

builder.Services.AddOpenApi();
builder.Services.AddVersoApplication();
builder.Services.AddVersoPersistence(builder.Configuration);
builder.Services.AddHealthChecks().AddVersoDatabaseCheck();

// Background jobs: the API only enqueues (Hangfire client); the Worker runs them. Never Chromium here.
builder.Services.AddVersoJobClient(builder.Configuration);

// Real-time notifications (plan §5.5). The relay reads finished work from the database and pushes it to SignalR.
builder.Services.AddSignalR();
builder.Services.AddSingleton<IPreviewNotifier, SignalRPreviewNotifier>();
builder.Services.AddHostedService<PreviewReadyRelay>();
builder.Services.AddSingleton<IExecutionNotifier, SignalRExecutionNotifier>();
builder.Services.AddHostedService<ExecutionEventRelay>();

// Archived documents are read for downloads (the Worker writes them); both use Documents:RootPath.
builder.Services.AddVersoDocumentStore(builder.Configuration);

var app = builder.Build();

app.UseCorrelationId();
app.UseHttpLogging();
app.UseExceptionHandler();
app.UseStatusCodePages();
if (corsOrigins.Length > 0)
{
    app.UseCors();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// The contract document: Development and Testing only (the contract test reads it).
if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
{
    app.MapOpenApi().AllowAnonymous();
}

app.MapHealthEndpoints();
app.MapAuthEndpoints();
app.MapTemplateEndpoints();
app.MapPdfPreviewEndpoints();
app.MapWorkflowEndpoints();
app.MapExecutionEndpoints();
app.MapDocumentEndpoints();
app.MapAuditEndpoints();
app.MapFolderInputEndpoints();
app.MapFolderLocationAdminEndpoints();
app.MapHub<ExecutionsHub>(ExecutionsHub.Path).RequireAuthorization(Policies.Read);

app.Run();

/// <summary>Entry point; public so integration tests can host the API with WebApplicationFactory.</summary>
public partial class Program;
