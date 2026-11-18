namespace Verso.Application.Executions;

/// <summary>The outcome of <see cref="SqlGuard.Check"/>: <c>Parameters</c> are the distinct <c>@name</c> placeholders in order of first use.</summary>
public sealed record SqlCheck(bool Ok, string? Code, string? Message, IReadOnlyList<string> Parameters);

/// <summary>
/// First safety net for the Query database step: accepts exactly one statement that starts with SELECT or WITH and contains no INTO, write or DDL keyword,
/// EXEC, EXECUTE or CALL. It tokenizes the text (comments, string literals and quoted identifiers are skipped), it does not search the raw text.
/// A safety net only: the connection's database login should itself be read-only.
/// </summary>
public static class SqlGuard
{
    public const int MaxLength = 4000;
    private const int MaxParameterLength = 64;

    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "DROP", "ALTER", "CREATE", "TRUNCATE", "GRANT", "REVOKE", "EXEC", "EXECUTE", "CALL",
    };

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

            if (c == '-' && Peek(sql, i + 1) == '-' && IsCommentEnd(Peek(sql, i + 2)))
            {
                while (i < sql.Length && sql[i] != '\n' && sql[i] != '\r') i++;
                continue;
            }
            if (c == '/' && Peek(sql, i + 1) == '*')
            {
                if (Peek(sql, i + 2) == '!') return Fail("sql.unsupported_syntax", "MySQL executable comments are not allowed.");
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

            if (c == '$' && StartsDollarQuote(sql, i)) return Fail("sql.unsupported_syntax", "Dollar-quoted strings are not allowed.");

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
                if (Forbidden.Contains(word)) return Fail("sql.not_select", "Only reading is allowed: INSERT, UPDATE, DELETE, MERGE, DDL, EXEC and CALL are not accepted.");
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

    // MySQL only starts a "--" comment when it is followed by whitespace or a control character.
    private static bool IsCommentEnd(char c) => char.IsWhiteSpace(c) || char.IsControl(c);

    // $$ or $tag$ (PostgreSQL dollar quoting). $1 and a$b are not.
    private static bool StartsDollarQuote(string s, int i)
    {
        if (Peek(s, i + 1) == '$') return true;
        var j = i + 1;
        if (!(char.IsLetter(Peek(s, j)) || Peek(s, j) == '_')) return false;
        while (char.IsLetterOrDigit(Peek(s, j)) || Peek(s, j) == '_') j++;
        return Peek(s, j) == '$';
    }

    private static char Peek(string s, int index) => index < s.Length ? s[index] : '\0';

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$' || c == '#';

    private static bool IsAsciiWordChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    private static SqlCheck Fail(string code, string message) => new(false, code, message, []);
}
