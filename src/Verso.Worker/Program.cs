using Hangfire;
using Verso.Application;
using Verso.Infrastructure;
using Verso.Infrastructure.Jobs;
using Verso.Rendering;
using Verso.Rendering.Pdf;
using Verso.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddVersoPersistence(builder.Configuration);
builder.Services.AddVersoWorkerUseCases();

// PDF printing with Chromium: only this process ever starts a browser (the API never does).
builder.Services.Configure<PdfPrinterOptions>(builder.Configuration.GetSection(PdfPrinterOptions.Section));
builder.Services.AddSingleton<ChromiumPdfPrinter>();
builder.Services.AddSingleton<IPdfPrinter>(sp => sp.GetRequiredService<ChromiumPdfPrinter>());
builder.Services.AddHostedService<PdfPrinterWarmUp>();

// Hangfire server: runs PDF preview jobs (and, from M7, executions).
builder.Services.AddVersoJobServer(builder.Configuration);
builder.Services.AddHostedService<WorkerHeartbeat>();
builder.Services.AddHostedService<RecurringJobsRegistration>();

var host = builder.Build();
host.Run();
