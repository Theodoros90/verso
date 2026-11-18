namespace Verso.Application.Executions;

/// <summary>The outcome of <see cref="SqlGuard.Check"/>: <c>Parameters</c> are the distinct <c>@name</c> placeholders in order of first use.</summary>
public sealed record SqlCheck(bool Ok, string? Code, string? Message, IReadOnlyList<string> Parameters);

/// <summary>
/// First safety net for the Query database step: accepts exactly one statement that starts with SELECT or WITH and contains no INTO,
/// EXEC, EXECUTE or CALL. It tokenizes the text (comments, string literals and quoted identifiers are skipped), it does not search the raw text.
/// It does not make a query safe on its own: the connection is expected to be read-only too.
/// </summary>
public static class SqlGuard
{
    public const int MaxLength = 4000;
    private const int MaxParameterLength = 64;

    public static SqlCheck Check(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return Fail("sql.empty", "Type a SELECT query.");
        if (sql.Length > MaxLength) return Fail("sql.too_long", $"The query is longer than {MaxLength} characters.");

        var parameters = new List<string>();
        string? first = null;
        var semicolon = false;
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '-' && Peek(sql, i + 1) == '-')
            {
                while (i < sql.Length && sql[i] != '\n' && sql[i] != '\r') i++;
                continue;
            }
            if (c == '/' && Peek(sql, i + 1) == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) return Fail("sql.unterminated", "A comment is not closed.");
                i = end + 2;
                continue;
            }

            // Anything that is not whitespace or a comment after a ';' is a second statement.
            if (semicolon) return Fail("sql.multiple_statements", "Only one statement is allowed.");

            if (c == ';') { semicolon = true; i++; continue; }

            if (c is '\'' or '"' or '`' or '[')
            {
                var close = c == '[' ? ']' : c;
                var j = i + 1;
                var closed = false;
                while (j < sql.Length)
                {
                    if (sql[j] == close)
                    {
                        if (Peek(sql, j + 1) == close) { j += 2; continue; } // doubled quote is an escape
                        closed = true;
                        break;
                    }
                    j++;
                }
                if (!closed) return Fail("sql.unterminated", "A string or quoted name is not closed.");
                first ??= "";
                i = j + 1;
                continue;
            }

            if (c == '@')
            {
                if (Peek(sql, i + 1) == '@')
                {
                    i += 2; // system variable such as @@ROWCOUNT, not a parameter
                    while (i < sql.Length && IsWordChar(sql[i])) i++;
                    first ??= "";
                    continue;
                }
                var j = i + 1;
                while (j < sql.Length && IsWordChar(sql[j])) j++;
                var name = sql[(i + 1)..j];
                if (name.Length == 0 || char.IsAsciiDigit(name[0]) || name.Length > MaxParameterLength || !name.All(IsAsciiWordChar))
                    return Fail("sql.bad_parameter", "A parameter name must start with a letter or underscore, use letters, digits and underscores, and be at most 64 characters.");
                if (!parameters.Contains(name)) parameters.Add(name);
                first ??= "";
                i = j;
                continue;
            }

            if (IsWordChar(c))
            {
                var j = i;
                while (j < sql.Length && IsWordChar(sql[j])) j++;
                var word = sql[i..j];
                if (first is null)
                {
                    if (!word.Equals("SELECT", StringComparison.OrdinalIgnoreCase) && !word.Equals("WITH", StringComparison.OrdinalIgnoreCase))
                        return Fail("sql.not_select", "The query must start with SELECT or WITH.");
                    first = word;
                }
                if (word.Equals("INTO", StringComparison.OrdinalIgnoreCase)) return Fail("sql.select_into", "SELECT ... INTO is not allowed.");
                if (word.Equals("EXEC", StringComparison.OrdinalIgnoreCase) || word.Equals("EXECUTE", StringComparison.OrdinalIgnoreCase)
                    || word.Equals("CALL", StringComparison.OrdinalIgnoreCase))
                    return Fail("sql.not_select", "EXEC and CALL are not allowed.");
                i = j;
                continue;
            }

            // punctuation and operators
            first ??= "";
            if (first.Length == 0) return Fail("sql.not_select", "The query must start with SELECT or WITH.");
            i++;
        }

        if (first is null) return Fail("sql.empty", "Type a SELECT query.");
        if (first.Length == 0) return Fail("sql.not_select", "The query must start with SELECT or WITH.");
        return new SqlCheck(true, null, null, parameters);
    }

    private static char Peek(string s, int index) => index < s.Length ? s[index] : '\0';

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$' || c == '#';

    private static bool IsAsciiWordChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    private static SqlCheck Fail(string code, string message) => new(false, code, message, []);
}
