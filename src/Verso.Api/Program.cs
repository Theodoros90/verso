using Microsoft.AspNetCore.HttpLogging;
using Verso.Api.Health;
using Verso.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

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

builder.Services.AddOpenApi();
builder.Services.AddVersoPersistence(builder.Configuration);
builder.Services.AddHealthChecks().AddVersoDatabaseCheck();

var app = builder.Build();

app.UseHttpLogging();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
else
{
    // TLS terminates at IIS; redirect any plain-HTTP request that reaches the app.
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.MapHealthEndpoints();

app.Run();

/// <summary>Entry point; public so integration tests can host the API with WebApplicationFactory.</summary>
public partial class Program;
