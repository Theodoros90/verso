using Hangfire;
using Verso.Application;
using Verso.Infrastructure;
using Verso.Infrastructure.Documents;
using Verso.Infrastructure.Jobs;
using Verso.Rendering;
using Verso.Rendering.Docx;
using Verso.Rendering.PdfCombine;
using Verso.Rendering.Pdf;
using Verso.Worker;

var builder = Host.CreateApplicationBuilder(args);

// Runs as a Windows service on servers (docs/runbook.md); from a console (dotnet run, tests) this does nothing.
// As a service, warnings and errors also go to the Windows Event Log (source "Verso.Worker"), and the content root is the exe folder.
builder.Services.AddWindowsService(options => options.ServiceName = "Verso Worker");

builder.Services.AddVersoPersistence(builder.Configuration);
builder.Services.AddVersoWorkerUseCases();

// PDF printing with Chromium: only this process ever starts a browser (the API never does).
builder.Services.Configure<PdfPrinterOptions>(builder.Configuration.GetSection(PdfPrinterOptions.Section));
builder.Services.AddSingleton<ChromiumPdfPrinter>();
builder.Services.AddSingleton<IPdfPrinter>(sp => sp.GetRequiredService<ChromiumPdfPrinter>());
builder.Services.AddHostedService<PdfPrinterWarmUp>();

// Word output: plain text blocks → .docx (Open XML SDK; no browser, no scripting).
builder.Services.AddSingleton<IDocxWriter, DocxWriter>();

// Combine PDFs: joins PDFs Verso generated itself (PDFsharp; no browser, no scripting).
builder.Services.AddSingleton<IPdfCombiner, PdfSharpCombiner>();

// Send email: the bank's SMTP relay from the Email section (ADR 0006); credentials only from the environment or a secret store.
builder.Services.AddVersoEmail(builder.Configuration);

// Print queue: PDFs and job files go to the spool folders in Print:Queues (ADR 0007); authors only name a queue key.
builder.Services.AddVersoPrintSpooler(builder.Configuration);

// Archived documents (local file system in the MVP; Documents:RootPath). Only the Worker writes.
builder.Services.AddVersoDocumentStore(builder.Configuration);

// Hangfire server: runs PDF previews, executions, and the recurring cleanup and execution-dispatch jobs.
builder.Services.AddVersoJobServer(builder.Configuration);
builder.Services.AddHostedService<WorkerHeartbeat>();
builder.Services.AddHostedService<RecurringJobsRegistration>();

// Folder input: approved folders from FolderInput:Locations, read as this service's identity (no stored passwords).
builder.Services.AddVersoFolderAccess(builder.Configuration);
builder.Services.AddHostedService<FolderInputService>();

var host = builder.Build();
host.Run();
