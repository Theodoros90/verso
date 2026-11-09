using Verso.Rendering.Pdf;

namespace Verso.Worker;

/// <summary>
/// Starts Chromium when the Worker starts, so the first PDF doesn't wait for the browser launch (several seconds).
/// A failure here (e.g. Chromium not installed) is logged with the fix; renders retry the launch themselves.
/// </summary>
internal sealed partial class PdfPrinterWarmUp(ChromiumPdfPrinter printer, ILogger<PdfPrinterWarmUp> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await printer.WarmUpAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogWarmUpFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Chromium could not be started; PDF rendering will fail until it can (see docs/setup.md, Chromium)")]
    private static partial void LogWarmUpFailed(ILogger logger, Exception exception);
}
