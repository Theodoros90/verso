using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Verso.Rendering.Binding;

namespace Verso.Rendering.Email;

/// <summary>The three presets of the Generate HTML email step. They change the heading label and accent colour, not the structure.</summary>
public enum EmailLayout
{
    Invoice,
    Statement,
    Reminder,
}

/// <summary>What a Generate HTML email step holds. Every text field may contain <c>{{path|filter}}</c> tokens (same grammar as file name patterns).</summary>
public sealed record EmailSpec(
    EmailLayout Layout, string Subject, string Preheader, string Body, string ButtonLabel, string ButtonUrl, string Footer, bool DarkMode);

/// <param name="Subject">One line, resolved.</param>
/// <param name="Html">A complete, self-contained document: table layout, inline styles, no scripts, no remote resources.</param>
/// <param name="PlainText">The plain-text alternative for the same message.</param>
public sealed record EmailResult(string Subject, string Html, string PlainText, IReadOnlyList<RenderIssue> Issues);

/// <summary>
/// Builds the body of an email from the step's text and the run's data. Pure: no I/O, no scripting. Resolved values are
/// plain text and are HTML-encoded exactly once on the way into the markup; the button link must resolve to http, https
/// or mailto (anything else, such as <c>javascript:</c>, is an error). Nothing is sent: the result is a document.
/// </summary>
public static class EmailRenderer
{
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);

    private static string Encode(string text) => Encoder.Encode(text);

    /// <summary>Encodes each line on its own (the encoder would turn a line break into a character reference) and joins them with <c>&lt;br&gt;</c>.</summary>
    private static string EncodeLines(string text) => string.Join("<br>", text.Split('\n').Select(Encode));

    private static (string Label, string Accent) Look(EmailLayout layout) => layout switch
    {
        EmailLayout.Statement => ("Statement", "#0E7C86"),
        EmailLayout.Reminder => ("Payment reminder", "#B45309"),
        _ => ("Invoice", "#1A3442"),
    };

    public static EmailResult Render(EmailSpec spec, JsonElement data)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var issues = new List<RenderIssue>();

        string Resolve(string field, string pattern)
        {
            var resolved = PatternText.Resolve(pattern ?? "", data);
            foreach (var path in resolved.Missing)
            {
                issues.Add(new(IssueSeverity.Error, "binding.missing", $"No value for '{path}' in {field}.", field, path));
            }

            return resolved.Text;
        }

        // A subject is a header: line breaks would let data start another header.
        var subject = string.Join(' ', Resolve("subject", spec.Subject).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        var preheader = string.Join(' ', Resolve("preheader", spec.Preheader).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        var body = Resolve("body", spec.Body).ReplaceLineEndings("\n").Trim();
        var label = Resolve("buttonLabel", spec.ButtonLabel).Trim();
        var url = Resolve("buttonUrl", spec.ButtonUrl).Trim();
        var footer = Resolve("footer", spec.Footer).ReplaceLineEndings("\n").Trim();

        if (subject.Length == 0)
        {
            issues.Add(new(IssueSeverity.Error, "email.subject_empty", "The subject is empty.", "subject", null));
        }

        if (body.Length == 0)
        {
            issues.Add(new(IssueSeverity.Error, "email.body_empty", "The body is empty.", "body", null));
        }

        var hasButton = label.Length > 0 && url.Length > 0;
        if (url.Length > 0 && !IsSafeUrl(url))
        {
            issues.Add(new(IssueSeverity.Error, "email.url_invalid", "The button link must be a full http, https or mailto address.", "buttonUrl", null));
            hasButton = false;
        }

        var (eyebrow, accent) = Look(spec.Layout);
        var paragraphs = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        if (spec.DarkMode)
        {
            html.Append("<meta name=\"color-scheme\" content=\"light dark\">\n<meta name=\"supported-color-schemes\" content=\"light dark\">\n");
        }

        html.Append("<title>").Append(Encode(subject)).Append("</title>\n<style>\n");
        html.Append("body{margin:0;padding:0;}\n@media only screen and (max-width:620px){.v-card{width:100%!important;}.v-pad{padding:20px!important;}}\n");
        if (spec.DarkMode)
        {
            html.Append("@media (prefers-color-scheme:dark){.v-bg{background-color:#111418!important;}.v-card{background-color:#1B2026!important;}.v-text{color:#E6EAEE!important;}.v-muted{color:#9AA4AE!important;}.v-rule{border-color:#2D353D!important;}}\n");
        }

        html.Append("</style>\n</head>\n<body style=\"margin:0;padding:0;background-color:#F2F4F6;\">\n");
        if (preheader.Length > 0)
        {
            html.Append("<div style=\"display:none;max-height:0;overflow:hidden;opacity:0;color:transparent;\">").Append(Encode(preheader)).Append("</div>\n");
        }

        html.Append("<table role=\"presentation\" class=\"v-bg\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background-color:#F2F4F6;\"><tr><td align=\"center\" style=\"padding:24px 12px;\">\n");
        html.Append("<table role=\"presentation\" class=\"v-card\" width=\"600\" cellpadding=\"0\" cellspacing=\"0\" style=\"width:600px;max-width:100%;background-color:#FFFFFF;border-top:4px solid ")
            .Append(accent).Append(";\">\n<tr><td class=\"v-pad\" style=\"padding:32px;font-family:Segoe UI,Helvetica,Arial,sans-serif;\">\n");
        html.Append("<div class=\"v-muted\" style=\"font-size:12px;letter-spacing:.08em;text-transform:uppercase;color:").Append(accent).Append(";font-weight:600;\">").Append(Encode(eyebrow)).Append("</div>\n");
        html.Append("<h1 class=\"v-text\" style=\"margin:8px 0 20px;font-size:22px;line-height:1.3;color:#1A2630;font-weight:600;\">").Append(Encode(subject)).Append("</h1>\n");
        foreach (var paragraph in paragraphs)
        {
            html.Append("<p class=\"v-text\" style=\"margin:0 0 16px;font-size:15px;line-height:1.6;color:#2B3742;\">")
                .Append(EncodeLines(paragraph)).Append("</p>\n");
        }

        if (hasButton)
        {
            html.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" style=\"margin:8px 0 24px;\"><tr><td style=\"background-color:").Append(accent)
                .Append(";border-radius:4px;\"><a href=\"").Append(Encode(url)).Append("\" style=\"display:inline-block;padding:12px 22px;font-size:15px;font-weight:600;color:#FFFFFF;text-decoration:none;\">")
                .Append(Encode(label)).Append("</a></td></tr></table>\n");
        }

        if (footer.Length > 0)
        {
            html.Append("<div class=\"v-rule\" style=\"border-top:1px solid #DDE2E7;padding-top:16px;\"><p class=\"v-muted\" style=\"margin:0;font-size:12px;line-height:1.5;color:#6B7782;\">")
                .Append(EncodeLines(footer)).Append("</p></div>\n");
        }

        html.Append("</td></tr>\n</table>\n</td></tr></table>\n</body>\n</html>\n");

        var text = new StringBuilder();
        text.Append(subject).Append("\n\n").Append(string.Join("\n\n", paragraphs)).Append('\n');
        if (hasButton)
        {
            text.Append('\n').Append(label).Append(": ").Append(url).Append('\n');
        }

        if (footer.Length > 0)
        {
            text.Append("\n--\n").Append(footer).Append('\n');
        }

        return new EmailResult(subject, html.ToString(), text.ToString(), issues);
    }

    private static bool IsSafeUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" or "mailto"
        && !url.Any(char.IsControl);
}
