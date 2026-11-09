using Microsoft.AspNetCore.HttpLogging;
using Verso.Api.Audit;
using Verso.Api.Common;
using Verso.Api.Health;
using Verso.Api.Templates;
using Verso.Application;
using Verso.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Fail closed: there is no authentication yet (Entra ID is planned), and the API has write endpoints.
// Until sign-in exists, the API only runs on developer machines and in automated tests.
var isLocal = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing");
if (!isLocal)
{
    throw new InvalidOperationException(
        $"Verso.Api has no authentication yet and refuses to run in the '{builder.Environment.EnvironmentName}' environment. " +
        "Use Development or Testing until Entra ID sign-in is implemented.");
}

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

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddVersoApplication();
builder.Services.AddVersoPersistence(builder.Configuration);
builder.Services.AddScoped<ICurrentUser, DevelopmentCurrentUser>();
builder.Services.AddHealthChecks().AddVersoDatabaseCheck();

var app = builder.Build();

app.UseHttpLogging();
app.UseExceptionHandler();
app.UseStatusCodePages();

// Development and Testing only (see the guard above); the contract test reads it.
app.MapOpenApi();

app.MapHealthEndpoints();
app.MapTemplateEndpoints();
app.MapAuditEndpoints();

app.Run();

/// <summary>Entry point; public so integration tests can host the API with WebApplicationFactory.</summary>
public partial class Program;
