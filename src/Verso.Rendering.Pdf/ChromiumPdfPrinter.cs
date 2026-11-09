using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace Verso.Rendering.Pdf;

public sealed class PdfPrinterOptions
{
    public const string Section = "Pdf";

    /// <summary>Upper bound for one document (load + print). The render fails with <see cref="TimeoutException"/> after it.</summary>
    public TimeSpan RenderTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Restart Chromium after this many documents, to bound memory growth.</summary>
    public int RecycleAfterRenders { get; set; } = 100;

    public int MaxConcurrentRenders { get; set; } = 2;

    /// <summary>
    /// Optional explicit chrome.exe. Normally empty: Playwright finds its pinned Chromium under PLAYWRIGHT_BROWSERS_PATH
    /// (offline bundle on servers) or the per-user default location (developer machines).
    /// </summary>
    public string? ExecutablePath { get; set; }

    /// <summary>Extra Chromium command-line switches (tests use a marker switch to find and kill the process).</summary>
    public IList<string> ExtraArgs { get; } = [];
}

/// <summary>
/// Prints with headless Chromium. One shared browser; every document gets a fresh, isolated browser context with
/// JavaScript disabled, offline mode on, and every request aborted (documents are self-contained: fonts and
/// images are data URIs). If Chromium crashes mid-render, it is restarted and the document is retried once.
/// </summary>
public sealed partial class ChromiumPdfPrinter : IPdfPrinter, IAsyncDisposable
{
    private readonly PdfPrinterOptions _options;
    private readonly ILogger<ChromiumPdfPrinter> _logger;
    private readonly SemaphoreSlim _concurrency;
    private readonly SemaphoreSlim _browserLock = new(1, 1);
    private IPlaywright? _playwright;
    private BrowserSlot? _current;
    private bool _disposed;

    public ChromiumPdfPrinter(IOptions<PdfPrinterOptions> options, ILogger<ChromiumPdfPrinter> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger;
        _concurrency = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentRenders));
    }

    /// <summary>Number of Chromium processes started so far (for diagnostics and tests).</summary>
    public int BrowserLaunches { get; private set; }

    /// <summary>Times the Playwright driver had to be replaced because it died.</summary>
    public int DriverResets { get; private set; }

    /// <summary>Test hook: the Playwright driver in use (the driver test disposes it to simulate the process dying).</summary>
    internal IPlaywright? Driver => _playwright;

    public async Task<PdfOutput> PrintAsync(PrintDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                BrowserSlot? slot = null;
                try
                {
                    slot = await AcquireAsync(cancellationToken).ConfigureAwait(false);
                    var (bytes, blocked) = await PrintOnceAsync(slot.Browser, document, cancellationToken).ConfigureAwait(false);
                    return new PdfOutput(bytes, blocked, attempt);
                }
                catch (Exception ex) when (attempt == 1 && !cancellationToken.IsCancellationRequested && IsBrowserFailure(slot, ex))
                {
                    LogBrowserCrashed(_logger, ex);
                    if (slot is not null)
                    {
                        slot.Dead = true;
                    }

                    if (IsDriverFailure(ex))
                    {
                        await ResetDriverAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    if (slot is not null)
                    {
                        await ReleaseAsync(slot).ConfigureAwait(false);
                    }
                }
            }
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private async Task<(byte[] Bytes, int Blocked)> PrintOnceAsync(IBrowser browser, PrintDocument document, CancellationToken cancellationToken)
    {
        var timeoutMs = (float)_options.RenderTimeout.TotalMilliseconds;
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            JavaScriptEnabled = false,
            Offline = true,
            ServiceWorkers = ServiceWorkerPolicy.Block,
            AcceptDownloads = false,
        }).ConfigureAwait(false);
        try
        {
            var blocked = 0;
            await context.RouteAsync("**/*", async route =>
            {
                Interlocked.Increment(ref blocked);
                await route.AbortAsync("blockedbyclient").ConfigureAwait(false);
            }).ConfigureAwait(false);

            var work = PrintPageAsync(context, document, timeoutMs);
            var bytes = await work.WaitAsync(_options.RenderTimeout, cancellationToken).ConfigureAwait(false);
            return (bytes, Volatile.Read(ref blocked));
        }
        finally
        {
            // Closing the context also aborts a render that timed out or was cancelled.
            try
            {
                await context.CloseAsync().ConfigureAwait(false);
            }
            catch (PlaywrightException)
            {
                // The browser is gone already; nothing to close.
            }
        }
    }

    /// <summary>Test hook: runs after the page loaded and before printing (the crash test kills Chromium here).</summary>
    internal Func<Task>? BeforePrintHook { get; set; }

    private async Task<byte[]> PrintPageAsync(IBrowserContext context, PrintDocument document, float timeoutMs)
    {
        var page = await context.NewPageAsync().ConfigureAwait(false);
        page.SetDefaultTimeout(timeoutMs);
        await page.SetContentAsync(DocumentFonts.IntoDocument(document.BodyHtml), new PageSetContentOptions
        {
            WaitUntil = WaitUntilState.Load,
            Timeout = timeoutMs,
        }).ConfigureAwait(false);

        if (BeforePrintHook is { } hook)
        {
            await hook().ConfigureAwait(false);
        }

        return await page.PdfAsync(new PagePdfOptions
        {
            Width = Mm(document.PaperWidthMm),
            Height = Mm(document.PaperHeightMm),
            Margin = new Margin { Top = Mm(document.MarginTopMm), Bottom = Mm(document.MarginBottomMm), Left = "0", Right = "0" },
            DisplayHeaderFooter = true,
            HeaderTemplate = DocumentFonts.IntoTemplate(document.HeaderTemplate),
            FooterTemplate = DocumentFonts.IntoTemplate(document.FooterTemplate),
            PrintBackground = true,
            PreferCSSPageSize = false,
        }).ConfigureAwait(false);
    }

    private static string Mm(decimal mm) => mm.ToString("0.###", CultureInfo.InvariantCulture) + "mm";

    private static bool IsBrowserFailure(BrowserSlot? slot, Exception ex) =>
        (slot is not null && !slot.Browser.IsConnected)
        || (ex is PlaywrightException pe && (pe.Message.Contains("closed", StringComparison.OrdinalIgnoreCase)
                                             || pe.Message.Contains("crash", StringComparison.OrdinalIgnoreCase)
                                             || pe.Message.Contains("disconnected", StringComparison.OrdinalIgnoreCase)
                                             || pe.Message.Contains("exited", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// "Process exited" is Playwright's own driver process going away (not just Chromium). The driver can't be
    /// reused after that: every call on it fails. It is replaced on the next attempt.
    /// </summary>
    private static bool IsDriverFailure(Exception ex) =>
        ex is PlaywrightException && ex.Message.Contains("Process exited", StringComparison.OrdinalIgnoreCase);

    /// <summary>Discards the Playwright driver (and its browser) so the next render starts both afresh.</summary>
    private async Task ResetDriverAsync()
    {
        await _browserLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_current is not null)
            {
                _current.Dead = true;
                _current.Retiring = true;
                await CloseIfIdleAsync(_current).ConfigureAwait(false);
                _current = null;
            }

            try
            {
                _playwright?.Dispose();
            }
            catch (PlaywrightException)
            {
                // Already gone.
            }

            _playwright = null;
            DriverResets++;
        }
        finally
        {
            _browserLock.Release();
        }
    }

    /// <summary>Starts Chromium ahead of the first document (the first launch takes seconds).</summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        var slot = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        slot.Renders--; // warm-up isn't a document
        await ReleaseAsync(slot).ConfigureAwait(false);
    }

    private async Task<BrowserSlot> AcquireAsync(CancellationToken cancellationToken)
    {
        await _browserLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_current is null || _current.Dead || _current.Retiring || !_current.Browser.IsConnected)
            {
                if (_current is not null)
                {
                    _current.Retiring = true;
                    await CloseIfIdleAsync(_current).ConfigureAwait(false);
                }

                _current = new BrowserSlot(await LaunchAsync().ConfigureAwait(false));
            }

            var slot = _current;
            slot.Active++;
            slot.Renders++;
            if (slot.Renders >= _options.RecycleAfterRenders)
            {
                slot.Retiring = true; // this render still uses it; the next one starts a new browser
            }

            return slot;
        }
        finally
        {
            _browserLock.Release();
        }
    }

    private async Task ReleaseAsync(BrowserSlot slot)
    {
        await _browserLock.WaitAsync().ConfigureAwait(false);
        try
        {
            slot.Active--;
            if (slot.Dead && ReferenceEquals(slot, _current))
            {
                slot.Retiring = true;
            }

            if (slot.Retiring || slot.Dead)
            {
                await CloseIfIdleAsync(slot).ConfigureAwait(false);
            }
        }
        finally
        {
            _browserLock.Release();
        }
    }

    private static async Task CloseIfIdleAsync(BrowserSlot slot)
    {
        if (slot.Active > 0 || slot.Closed)
        {
            return;
        }

        slot.Closed = true;
        try
        {
            await slot.Browser.CloseAsync().ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            // Already gone (crashed or killed).
        }
    }

    private async Task<IBrowser> LaunchAsync()
    {
        _playwright ??= await Playwright.CreateAsync().ConfigureAwait(false);
        var args = new List<string> { "--disable-extensions", "--disable-background-networking", "--no-first-run", "--disable-sync" };
        args.AddRange(_options.ExtraArgs);
        try
        {
            var browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                ExecutablePath = string.IsNullOrWhiteSpace(_options.ExecutablePath) ? null : _options.ExecutablePath,
                Args = args,
            }).ConfigureAwait(false);
            BrowserLaunches++;
            LogBrowserStarted(_logger, browser.Version);
            return browser;
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Chromium for Playwright is not installed. See docs/setup.md, section \"Chromium\" (offline bundle on servers). " +
                "The Worker never downloads browsers at runtime.", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_current is not null)
        {
            _current.Active = 0;
            await CloseIfIdleAsync(_current).ConfigureAwait(false);
        }

        _playwright?.Dispose();
        _concurrency.Dispose();
        _browserLock.Dispose();
    }

    private sealed class BrowserSlot(IBrowser browser)
    {
        public IBrowser Browser { get; } = browser;
        public int Active { get; set; }
        public int Renders { get; set; }
        public bool Retiring { get; set; }
        public bool Dead { get; set; }
        public bool Closed { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Chromium {Version} started for PDF printing")]
    private static partial void LogBrowserStarted(ILogger logger, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chromium failed during a render; restarting it and retrying the document once")]
    private static partial void LogBrowserCrashed(ILogger logger, Exception exception);
}
