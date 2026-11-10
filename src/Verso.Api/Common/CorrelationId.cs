using System.Text.RegularExpressions;

namespace Verso.Api.Common;

/// <summary>
/// Every request gets a correlation id: the caller's <c>X-Correlation-Id</c> when it is well-formed, otherwise a new
/// one. It is echoed in the response header and added to the logging scope of everything the request logs, and a
/// submission stores it on the execution (the Worker logs it too), so one id follows a run through both processes.
/// </summary>
public static partial class CorrelationId
{
    public const string Header = "X-Correlation-Id";
    private const string ItemKey = "Verso.CorrelationId";

    [GeneratedRegex(@"^[A-Za-z0-9._:\-]{1,64}$")]
    private static partial Regex Valid();

    public static string Get(HttpContext http) => http.Items[ItemKey] as string ?? "";

    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        var supplied = http.Request.Headers[Header].ToString();
        var id = Valid().IsMatch(supplied) ? supplied : Guid.NewGuid().ToString("N");
        http.Items[ItemKey] = id;
        http.Response.Headers[Header] = id;

        var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Verso.Request");
        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id }))
        {
            await next(http);
        }
    });
}
